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

/// Both registries are FIXED-layout tables: the columns are declared once in the header
/// and every row obeys them, so what a row contains can never move a column — a launch
/// that widens `status` cannot squeeze `name` into a second line, and the two tables hang
/// their controls on the same right rail. The name column takes whatever is left.
module private Col =
    let table = "w-full text-left border-collapse table-fixed"
    /// Wide enough for a whole Crockford id — a half id identifies nothing, so this column is
    /// sized never to truncate. MEASURED, because the number depends on the face: 26 characters
    /// of 12px Monaspace Neon is 194px, plus the cell's own 16px gutter. It was 204px when the
    /// ids were set in whatever monospace a box happened to have, and the switch to a shipped
    /// face made them 6px too wide for it. The first thing to go on a phone.
    let id = "w-[210px] max-md:hidden"
    /// Holds the status word plus `port · pid` uncut; on a phone, just the word.
    let status = "w-[256px] max-md:w-[100px]"
    /// `2026-08-18 09:12Z` in the 12px mono face, plus the cell's gutter. MEASURED like the
    /// id beside it: 17 characters of 12px Monaspace Neon is 123px, and the caps header over
    /// it is narrower than that. Goes with the id on a phone — it is the sort KEY, and a sort
    /// you cannot see is still one you can read off the order of the rows.
    let created = "w-[144px] max-md:hidden"
    /// One `h-8` control, full-bleed in its column, plus the cell's left gutter. The
    /// session rail can be tighter on a phone — Launch and Stop are short words, and
    /// what the rail spares there the name gets — where Withdraw is not.
    let actions = "w-[128px]"
    /// The session rail carries the lifecycle verb AND the archive icon beside it: the
    /// 128px text button, a 24px borderless icon, and the 8px between them.
    let actionsNarrow = "w-[160px] max-md:w-[136px]"

// The status word carries the colour (text, never boxed — the affordance rule); the
// process detail (port · pid) is plumbing, so it sits beside the word in the quieter
// faint mono step rather than shouting in the status voice, and yields on narrow
// screens. It stays on ONE line at every width its column is given: a status that
// wraps when a session starts is a row that changes height when a session starts.
let private statusView (view: ProcessManager.SessionView) : TemplateResult =
    match view.Record.ArchivedAt, view.Status with
    // An operator's decision, not a process state — but it belongs in this cell, because
    // "archived" is the answer a reader wants here and "stopped" for a session that can no
    // longer start is true and useless.
    | Some _, _ ->
        html $"""<span class="{Style.statusFaint}" data-status="{Dom.Manager.statusArchived}">archived</span>"""
    | None, ProcessManager.NotRunning ->
        html $"""<span class="{Style.statusFaint}" data-status="{Dom.Manager.statusStopped}">stopped</span>"""
    | None, ProcessManager.Running (port, pid) ->
        html
            $"""<span class="{Style.statusOk}" data-status="{Dom.Manager.statusRunning}"><span class="{Style.statusDotPulse}"></span>running</span><span class="font-terminal text-code-sm text-ink-faint tabular-nums ml-2.5 max-md:hidden">port {port} · pid {pid}</span>"""
    | None, ProcessManager.Exited code ->
        let reason = code |> Option.map string |> Option.defaultValue "signal"
        html $"""<span class="{Style.statusErr}" data-status="{Dom.Manager.statusExited}">exited ({reason})</span>"""

/// The name cell. Opening a running session is THE act on it, and it is carried by the
/// name — content is the interface — rather than by a second bordered rectangle in the
/// right rail: with five sessions listed, a per-row Open plus a per-row lifecycle verb is
/// ten buttons competing with the page's one real CTA (Create). A stopped session has
/// nothing to open yet, so its name is plain text and its row's one control says Launch.
let private nameView (access: PublicAccess) (view: ProcessManager.SessionView) : TemplateResult =
    match view.Status with
    | ProcessManager.Running (port, _) when view.Record.ArchivedAt.IsNone ->
        // A plain URL: access is authorized by the OIDC bounce (session -> manager ->
        // back), not by a token in the link. The origin is the configured public one
        // (docs/plans/09) so a remote browser gets a link it can follow; loopback when
        // unset.
        // The address comes from the deployment's session template (docs/plans/10), the
        // same declaration the session itself used to register its redirect URI.
        let openUrl = sprintf "%s/" (PublicAccess.sessionAddress view.Record.SessionId port access).Url
        html
            $"""<a class="{Style.recordLink}" href="{openUrl}" target="_blank" data-open>{view.Record.DisplayName}<span class="{Style.recordLinkMark}" aria-hidden="true">↗</span></a>"""
    | ProcessManager.Running _
    | ProcessManager.NotRunning
    | ProcessManager.Exited _ -> html $"""<span class="{Style.body}">{view.Record.DisplayName}</span>"""

/// The row's controls: the lifecycle verb this session is currently capable of, and — where
/// the session is not already archived — the quiet way to retire it. Both text faces rest on
/// the quiet rim and answer to the pointer (Stop turns err, Launch turns ink), so a column of
/// them reads as a rail of outlines rather than a column of CTAs.
///
/// The archive verb is a `btnIconBare`, whose rule describes exactly this case: borderless and
/// faint at rest, because the ROW is the subject. That is what lets a second control onto the
/// rail without breaking the one-CTA-per-row reading above — a second OUTLINED button would
/// have made a five-session list ten competing rectangles, which is the trade `nameView`
/// already refused once.
///
/// An ARCHIVED row offers Unarchive and nothing else: no Launch, because a control whose only
/// outcome is a refusal is worse than no control, and no archive icon, because the word it
/// wears is already the act.
let private actions (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    let name = view.Record.DisplayName
    match view.Record.ArchivedAt with
    | Some _ ->
        html $"""<button type="button" class="{Style.btn} w-full" data-unarchive="{id}">Unarchive</button>"""
    | None ->
        let verb =
            match view.Status with
            | ProcessManager.Running _ ->
                html $"""<button type="button" class="{Style.btnDanger} flex-1 min-w-0" data-stop="{id}">Stop</button>"""
            | ProcessManager.NotRunning
            | ProcessManager.Exited _ ->
                html $"""<button type="button" class="{Style.btn} flex-1 min-w-0" data-launch="{id}">Launch</button>"""
        html $"""
            <div class="flex items-center gap-2">
              {verb}
              <button type="button" class="{Style.btnIconBare}" data-archive="{id}"
                      aria-label="Archive {name}" title="Archive {name}">{Icon.archive}</button>
            </div>"""

/// When the session was registered — the key the registry is ordered by, so it is shown.
///
/// ABSOLUTE, not relative. A render function that said "3d ago" would need a clock threaded
/// through the whole render layer, and these frames are pushed only when something CHANGES,
/// so a "2m ago" would sit on the screen going quietly wrong for hours. UTC, marked `Z` so it
/// is never ambiguous, with the full offset-bearing timestamp on the `<time>` for anything
/// that reads it properly.
let private createdView (at: System.DateTimeOffset) : TemplateResult =
    let shown = sprintf "%04d-%02d-%02d %02d:%02d" at.Year at.Month at.Day at.Hour at.Minute
    let iso = at.ToString "o"
    html $"""<time datetime="{iso}" title="{iso}">{shown}Z</time>"""

/// One session row — an action's swap unit: launch/stop replace it wholesale, so the markup is
/// always a pure function of the Manager's current view. (Live status replaces the whole table
/// instead; see the rows stream.) The human name leads (content is the interface); the minted
/// id is plumbing, faint mono, and yields on narrow screens. Actions anchor the right edge so
/// the row reads name → state → verb.
///
/// A row is the same height whatever its state. The table is fixed-layout (`Col`), so
/// launching cannot widen the status column and squeeze a name into a second line, and each
/// cell holds ONE line — a single `h-8` control in the action column, ellipsis rather than
/// wrap in the text ones. Rows that jump as processes start and stop make a list you cannot
/// keep your place in, and put a control under a pointer that was aimed at its neighbour.
let private rowTemplate (access: PublicAccess) (view: ProcessManager.SessionView) : TemplateResult =
    let id = SessionId.value view.Record.SessionId
    html $"""
        <tr class="border-b border-hair hover:bg-surface transition-colors" data-session="{id}">
          <td class="py-3 pr-4 align-middle truncate" title="{view.Record.DisplayName}">{nameView access view}</td>
          <td class="py-3 pr-4 align-middle font-terminal text-code text-ink-faint truncate max-md:hidden">{id}</td>
          <td class="py-3 pr-4 align-middle font-terminal text-code text-ink-faint tabular-nums truncate max-md:hidden">{createdView view.Record.CreatedAt}</td>
          <td class="py-3 pr-4 align-middle truncate">{statusView view}</td>
          <td class="py-3 pl-4 align-middle">{actions view}</td>
        </tr>"""

/// One filter chip. A real `<a>`, because the filter IS the page's location: it lives in the
/// URL, a bookmark restores it, and the back button undoes it. That makes it keyboard-operable
/// and focusable with no help, and `aria-current` says which states are being shown to anything
/// that cannot see which chips are lit.
///
/// Its href is computed HERE, server-side, from `SessionQuery.toQueryString` — the page's script
/// never builds a URL, so there is exactly one encoder and a chip can never link somewhere the
/// rows beside it disagree with.
let private filterChip (query: SessionQuery) (state: ArchiveState) : TemplateResult =
    let shown = query.Show.Contains state
    let word, key =
        match state with
        | Active -> "active", "show-active"
        | Archived -> "archived", "show-archived"
    let href = sprintf "?%s" (SessionQuery.toQueryString (SessionQuery.toggling state query))
    let face = if shown then Style.filterChipOn else Style.filterChipOff
    if shown then
        html $"""<a class="{face}" href="{href}" data-filter="{key}" aria-current="true">{word}</a>"""
    else
        html $"""<a class="{face}" href="{href}" data-filter="{key}">{word}</a>"""

// The swap unit for a create is the whole section (filter, count, table), so the count, the
// empty state and the controls can never go stale against the rows they describe. Which is
// also why this takes the QUERY and does the filtering itself: a caller that filtered on its
// own could hand these chips a list they do not describe.
let private tableTemplate
    (access: PublicAccess)
    (query: SessionQuery)
    (all: ProcessManager.SessionView list)
    : TemplateResult =
    let views = SessionQuery.apply query (fun (v: ProcessManager.SessionView) -> v.Record) all
    // Nothing to show has two quite different causes, and saying the wrong one sends somebody
    // looking for a bug. An empty REGISTRY is "no sessions yet"; a filter that hid everything
    // says so, and names what it is hiding, with the chip that reveals it directly above.
    let emptyWord =
        match all, query.Show.IsEmpty with
        | [], _ -> "no sessions yet"
        | _, true -> "no filter selected — pick active or archived above"
        | _, false ->
            let hidden = List.length all - List.length views
            sprintf "no sessions match — %d hidden" hidden
    let rows =
        match views with
        | [] ->
            [ html $"""
                <tr>
                  <td colspan="5" class="py-10 text-center {Style.small}">{emptyWord}</td>
                </tr>""" ]
        | views -> views |> List.map (rowTemplate access)
    // The `created` header IS the sort control — the canonical accessible table sort, and it
    // spends no chrome anywhere else on the page. When last-activity or a summary arrives, its
    // header becomes the second one of these and nothing here has to be redesigned.
    let sortHref = sprintf "?%s" (SessionQuery.toQueryString (SessionQuery.reversed query))
    let sortMark, sortedBy =
        match query.Order with
        | NewestFirst -> "↓", "descending"
        | OldestFirst -> "↑", "ascending"
    html $"""
        <section class="flex flex-col gap-3" data-sessions>
          <div class="flex items-baseline gap-2.5 flex-wrap">
            <span class="{Style.label}">sessions</span>
            <span class="font-semibold text-[11px] leading-4 tracking-[0.18em] text-ink-faint tabular-nums">{List.length views}</span>
            <div class="flex items-center gap-1.5 ml-2" role="group" aria-label="Show sessions">
              {filterChip query Active}
              {filterChip query Archived}
            </div>
          </div>
          <table class="{Col.table}">
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.id}">id</th>
                <th scope="col" class="py-2 pr-4 {Col.created}" aria-sort="{sortedBy}">
                  <a class="{Style.sortHeader}" href="{sortHref}" data-filter="sort">created <span aria-hidden="true">{sortMark}</span></a>
                </th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.status}">status</th>
                <th scope="col" class="py-2 pl-4 {Col.actionsNarrow}"><span class="sr-only">actions</span></th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
        </section>"""

/// A rendered fragment (a single row), served as an action's answer.
let sessionRow (access: PublicAccess) (view: ProcessManager.SessionView) : string =
    Ssr.render (rowTemplate access view)

/// A rendered fragment (the whole table), served after a create or an archive and pushed on
/// the rows stream. Takes the reader's QUERY and filters with it, so the chips it draws and
/// the rows under them are one render and cannot describe different lists.
let sessionsTable (access: PublicAccess) (query: SessionQuery) (views: ProcessManager.SessionView list) : string =
    Ssr.render (tableTemplate access query views)

// The interactivity, without htmx: a tiny vanilla script that swaps fragments on
// create/launch/stop and takes live status from the rows stream. Inline (no external src) so
// the page is self-contained — local first, no CDN.
let private script =
    """
    const swap = (el, htmlText) => {
      const t = document.createElement('template'); t.innerHTML = htmlText.trim()
      const n = t.content.firstElementChild
      if (!n || !el || n.outerHTML === el.outerHTML) return
      // Keyboard continuity (WCAG 2.0): replacing the focused element strands focus on
      // <body>. Land it back on the SAME session — the swap unit is often the whole table
      // (the rows stream, a create), and the replacement's first action belongs to the
      // first row, which is another session's control under the same finger.
      const active = el.contains(document.activeElement) ? document.activeElement : null
      const row = active && active.closest('[data-session]')
      // A filter or sort control keeps a STABLE name across a swap even though its href just
      // flipped, so the hand that pressed `archived` is left on `archived` rather than being
      // dropped onto the first row of the list it just asked for.
      const filter = active && active.closest('[data-filter]')
      const wasAction = !!active && (active.hasAttribute('data-launch') || active.hasAttribute('data-stop'))
      el.replaceWith(n)
      if (!active) return
      const find = (sel) => sel && (n.matches(sel) ? n : n.querySelector(sel))
      if (filter) {
        const back = find('[data-filter="' + CSS.escape(filter.getAttribute('data-filter')) + '"]')
        if (back) { back.focus(); return }
      }
      const sel = row && '[data-session="' + CSS.escape(row.getAttribute('data-session')) + '"]'
      const scope = find(sel) || n
      // A row whose state just changed has swapped its verb (Launch <-> Stop): follow the
      // ACT, not the word, so the hand that pressed Launch is left on Stop.
      const f = (wasAction && scope.querySelector('[data-launch],[data-stop]')) || scope.querySelector('a[href], button, input')
      if (f) f.focus()
    }
    const sessionsEl = () => document.querySelector('[data-sessions]')
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-launch],[data-stop]'); if (!b) return
      const id = b.getAttribute('data-launch') || b.getAttribute('data-stop')
      const action = b.hasAttribute('data-launch') ? 'launch' : 'stop'
      const row = b.closest('tr')
      const r = await fetch('/sessions/' + id + '/' + action, { method: 'POST' })
      if (r.ok) swap(row, await r.text())
    })
    // Archiving answers with the WHOLE table, not a row: it can move a session out of the
    // filter you are looking at, so the row is no longer the unit that changed. The query
    // rides along because the answer has to be rendered for the list this page is showing.
    document.addEventListener('click', async (e) => {
      const b = e.target.closest('[data-archive],[data-unarchive]'); if (!b) return
      const id = b.getAttribute('data-archive') || b.getAttribute('data-unarchive')
      const action = b.hasAttribute('data-archive') ? 'archive' : 'unarchive'
      const r = await fetch('/sessions/' + id + '/' + action + location.search, { method: 'POST' })
      if (r.ok) swap(sessionsEl(), await r.text())
    })
    // A filter or sort click is a navigation this page performs itself: adopt the href the
    // SERVER computed as the new location, then reopen the stream at it. The first frame is
    // the whole snapshot for the new query, so one click is one swap and no page reloads —
    // which is also why focus is never stranded.
    document.addEventListener('click', (e) => {
      const a = e.target.closest('[data-filter]'); if (!a) return
      if (e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return
      e.preventDefault()
      history.replaceState(null, '', a.getAttribute('href'))
      openRows()
    })
    document.addEventListener('submit', async (e) => {
      const f = e.target.closest('[data-create-session]'); if (!f) return
      e.preventDefault()
      const r = await fetch('/sessions' + location.search, { method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' }, body: new URLSearchParams(new FormData(f)) })
      if (r.ok) { swap(sessionsEl(), await r.text()); f.reset() }
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
    // The stream is opened AT the current query and reopened when it changes; the server
    // filters per connection, so live status keeps arriving under whatever is being shown.
    let rows = null
    const openRows = () => {
      if (rows) rows.close()
      rows = new EventSource('/sessions/rows' + location.search)
      rows.onmessage = (e) => { if (e.data) swap(sessionsEl(), e.data) }
    }
    openRows()
    // The back button moves the filter, so the stream has to move with it.
    window.addEventListener('popstate', openRows)
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
          <td class="py-3 pr-4 align-middle {Style.body} truncate" title="{name}">{name}</td>
          <td class="py-3 pr-4 align-middle font-terminal text-code text-ink-faint truncate max-md:hidden"
              title="{McpTransport.describe declaration.Server.Transport}">{McpTransport.describe declaration.Server.Transport}</td>
          <td class="py-3 pr-4 align-middle {Style.small} truncate" title="{audience}">{audience}</td>
          <td class="py-3 pl-4 align-middle">
            <button type="button" class="{Style.btnDanger} w-full" data-mcp-withdraw="{name}" data-mcp-audience="{audienceValue}">Withdraw</button>
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
                  <td colspan="4" class="py-10 text-center {Style.small}">no MCP servers — a session reaches only its own tools</td>
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
          <table class="{Col.table}">
            <thead>
              <tr class="border-b border-hair">
                <th scope="col" class="py-2 pr-4 {Style.label}">name</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.id}">address</th>
                <th scope="col" class="py-2 pr-4 {Style.label} {Col.status}">reaches</th>
                <th scope="col" class="py-2 pl-4 {Col.actions}"><span class="sr-only">actions</span></th>
              </tr>
            </thead>
            <tbody>{rows}</tbody>
          </table>
          <form class="flex flex-col gap-3 pt-3" data-declare-mcp>
            <div class="flex flex-wrap items-end gap-3">
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-name">name</label>
                <input id="mcp-name" name="name" placeholder="serial" autocomplete="off" required
                  class="{Style.fieldOf "w-40 max-w-full"}">
              </div>
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-url">address</label>
                <input id="mcp-url" name="url" type="url" placeholder="http://127.0.0.1:7333" autocomplete="off" required
                  class="{Style.fieldOf "w-72 max-w-full"}">
              </div>
              <div class="flex flex-col gap-1.5">
                <label class="{Style.label}" for="mcp-audience">reaches</label>
                <div class="{Style.fieldSelectWrapOf "w-56 max-w-full"}">
                  <select id="mcp-audience" name="session" class="{Style.fieldSelect}">
                    <option value="">any session</option>
                    {options}
                  </select>
                  <span class="{Style.fieldSelectMark}">{Icon.down}</span>
                </div>
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
// shell is `Style.app` (the visible viewport's height, overflow-hidden), so <main> owns the
// scrolling — a long registry scrolls under a fixed viewport instead of clipping.
let private bodyTemplate
    (access: PublicAccess)
    (query: SessionQuery)
    (views: ProcessManager.SessionView list)
    (declarations: McpDeclaration list)
    : TemplateResult =
    html $"""
        <main class="flex-1 min-w-0 overflow-y-auto">
          <!-- The registry's measure, not a reading column's. `name` is the only elastic
               column, and it gets whatever the fixed ones leave: at the 896px this used to
               be, five columns (name, id, created, status, and a rail carrying two controls)
               left it 38px and every session on the page rendered as a single letter and an
               ellipsis. 1152px leaves it 318px — wider than the 254px it had when there were
               four — and gives the MCP table's address column room it was already short of. -->
          <div class="max-w-6xl w-full mx-auto flex flex-col px-8 max-md:px-4">
            <header class="h-[88px] shrink-0 flex items-end pb-5 border-b border-hair">
              <h1 class="{Style.wordmark}">yession<span class="text-green">.</span> <span class="{Style.label}">manager</span></h1>
            </header>
            <!-- The id is minted server-side (a Docker-safe Crockford id); only a human
                 name is entered here. -->
            <form class="flex flex-col gap-3 pt-6 pb-8" data-create-session>
              <label class="{Style.label}" for="new-session-name">new session</label>
              <div class="flex flex-wrap items-center gap-3">
                <input id="new-session-name" name="name" placeholder="display name" autocomplete="off"
                  class="{Style.fieldOf "w-72 max-w-full"}">
                <button type="submit" class="{Style.btnPrimary}">Create</button>
              </div>
            </form>
            <div class="pb-10">{tableTemplate access query views}</div>
            <div class="pb-10">{mcpTemplate views declarations}</div>
          </div>
        </main>"""

/// `styleSheetUrl` is passed in rather than read from the module below: F# scopes top-down, and
/// the stylesheet's address is derived from bytes read further down the file.
let page
    (styleSheetUrl: string)
    (access: PublicAccess)
    (query: SessionQuery)
    (views: ProcessManager.SessionView list)
    (declarations: McpDeclaration list)
    : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        "<title>Yession Manager</title>"
        Style.headTags styleSheetUrl
        WebApp.managerHeadTags (SessionRoute.relative Icon)
        sprintf "</head><body class=\"%s\">" Style.app
        Ssr.render (bodyTemplate access query views declarations)
        sprintf "<script>%s</script>" script
        "</body></html>"
    ]

// --- Routing ----------------------------------------------------------------------------

[<Fable.Core.Emit("new URL($0, 'http://local').pathname")>]
let private pathnameOf (url: string) : string = Fable.Core.Util.jsNative

[<Fable.Core.Emit("Object.fromEntries(new URLSearchParams($0))[$1] ?? ''")>]
let private formField (body: string) (name: string) : string = Fable.Core.Util.jsNative

/// Where the built asset set lives when this is not an installed package. One variable for
/// the whole directory (`Assets`), which is also why this page can link a stylesheet whose
/// faces this file has never heard of.
let private assetsDir = envOr "YESSION_ASSETS" "app/out/public/assets"

/// The same static asset service the Session Process runs, over this process's OWN set — read
/// and addressed once at boot rather than per request, so every render of this page (it is
/// rendered per request) names the same bytes.
let private assets = Assets.load assetsDir

let private cssUrl = Assets.url assets AssetFile.``app``

/// The icon's constant is base64 (it lives in source); the wire wants the PNG. Same decode
/// the session server does, for the same reason — `res.end` takes what Node's `end` takes.
[<Fable.Core.Emit("Buffer.from($0, 'base64')")>]
let private decodeBase64 (encoded: string) : string = Fable.Core.Util.jsNative

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
<h1>Opening session…</h1>
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
          'The session started, but its address is not answering after 20 seconds. ' +
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
    // What this reader asked to see. Parsed off the request rather than held anywhere, so the
    // page, the stream and every action that answers with a table agree by construction —
    // and a bookmark renders correctly on first paint with no client state machine.
    let query = SessionQuery.ofQueryString req.url
    let tableNow () = sessionsTable pm.Public query (pm.Sessions ())
    let rowOf (sessionId: SessionId) =
        match pm.TryFind sessionId with
        | Some view -> sessionRow pm.Public view
        | None -> ""
    // A refusal over an ARCHIVED session is not a server fault — it is a conflict with
    // durable state the caller can resolve, which matters most for `/open`, the URL a session
    // client's reconnect card points at. This picks the STATUS CODE and nothing else: the
    // refusal itself is `ManagerState.launchable`'s, and its words are what gets sent, so the
    // rule and the code it is reported under cannot drift apart.
    let refusalStatus (sessionId: SessionId) =
        match pm.TryFind sessionId with
        | Some view when view.Record.ArchivedAt.IsSome -> 409
        | _ -> 500
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
                    | Error reason -> respond res (refusalStatus sessionId) "text/plain" reason
                })
    // Route first (pure — did the UI claim this path?), authenticate second: the gate
    // runs once, ahead of every claimed route, and unclaimed paths fall through to the
    // composing server untouched.
    let route : (unit -> unit) option =
        match req.``method``, path with
        | "GET", "/" ->
            Some (fun () -> html res (page cssUrl pm.Public query (pm.Sessions ()) (pm.McpServers ())))
        | "GET", path when path.StartsWith ("/" + SessionRoute.assetsPrefix) ->
            // Everything static this build ships, served by path and by nothing else — the
            // same service the Session Process runs, over this process's own set. The Manager
            // page links the stylesheet, and the stylesheet names its own faces; neither this
            // route nor this file knows what those are, which is the point: a build that adds
            // an asset adds a file, not a case.
            //
            // The Manager has no `<base href>` and always sits at its origin root, so the
            // relative addresses the page and the stylesheet emit resolve to exactly here.
            match SessionRoute.parse "GET" path with
            | Some (Asset (build, file)) -> Some (fun () -> Assets.serve assets build file res)
            | _ -> None
        | "GET", path when path = "/" + SessionRoute.relative Icon ->
            // The same mark the session shells wear, from the same constant — the Manager
            // sits at its origin root, so the relative address the page emits is this path.
            Some (fun () ->
                res.writeHead (
                    200,
                    createObj [ "content-type", box "image/png"; "cache-control", box CachePolicy.shell ])
                |> ignore
                res.``end`` (decodeBase64 WebApp.iconPngBase64))
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
                    | Ok _ -> html res (tableNow ())
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
                Some (fun () -> Sse.stream req res (sessionsTable pm.Public query) pm.SubscribeSessions |> ignore)
            | "POST", [| id; "launch" |] ->
                Some (fun () ->
                    sessionAction id (fun sessionId ->
                        async {
                            let! outcome = pm.Launch sessionId
                            return outcome |> Result.map ignore
                        }))
            | "POST", [| id; "stop" |] ->
                Some (fun () -> sessionAction id pm.Stop)
            // Archiving answers with the WHOLE table rather than a row: it can move a session
            // out of the filter the caller is looking at, so the row is no longer the unit
            // that changed. (The verbs also publish, which is what reaches every OTHER open
            // page and the registry stream — the same split `launch` already makes.)
            | "POST", [| id; "archive" |] ->
                Some (fun () ->
                    match SessionId.create id with
                    | Error e -> respond res 400 "text/plain" e
                    | Ok sessionId ->
                        Async.StartImmediate (
                            async {
                                match! pm.Archive sessionId with
                                | Ok () -> html res (tableNow ())
                                | Error reason -> respond res 500 "text/plain" reason
                            }))
            | "POST", [| id; "unarchive" |] ->
                Some (fun () ->
                    match SessionId.create id with
                    | Error e -> respond res 400 "text/plain" e
                    | Ok sessionId ->
                        match pm.Unarchive sessionId with
                        | Ok () -> html res (tableNow ())
                        | Error reason -> respond res 500 "text/plain" reason)
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
                                    | Error reason -> respond res (refusalStatus sessionId) "text/plain" reason
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
