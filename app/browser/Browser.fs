module Yession.Browser.Main

// The browser client entry: the same Elmish/Ylmish program as everywhere else, wired to
// the Session Process over a *native* WebRTC data channel (the Node tests use
// libdatachannel; the protocol and signalling are identical). The shell is rendered by
// Fable.Lit — `View.view` into `#app` on every model change. Lit diffs the DOM, so focus,
// caret, and the collaborative textareas survive re-renders with no manual bookkeeping;
// interactive controls are inline template handlers, not attribute delegation.

open System
open Elmish
open Fable.Core
open Fable.Core.JsInterop
open Yjs
open Yession.Domain
open Yession.App
open Lit

// --- Native WebRTC (non-trickle, mirroring app/WebRtc.fs) -----------------------------

// Opening the data channel, as a TOTAL function: it settles with the channel, or with why it
// could not be had. It used to resolve only on `dc.onopen`, so a signalling POST that failed
// — or a session that simply was not there — left this promise pending forever and the shell
// stuck on "connecting" with nothing to say and nothing to do.
//
// `timeoutMs` bounds the whole handshake (offer, gathering, answer, channel open); it is the
// difference between "not connected, the session did not answer" and an eternal wait.
[<Emit("""new Promise((resolve) => {
  const pc = new RTCPeerConnection({ iceServers: [] })
  const dc = pc.createDataChannel('session')
  let settled = false
  const succeed = () => { if (!settled) { settled = true; resolve({ ok: true, channel: dc, timedOut: false, detail: '' }) } }
  const fail = (timedOut, detail) => {
    if (settled) return
    settled = true
    try { pc.close() } catch {}
    resolve({ ok: false, channel: null, timedOut, detail: String(detail) })
  }
  let sent = false
  const send = async () => {
    if (sent || settled) return
    sent = true
    try {
      const reply = await fetch($0, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ type: pc.localDescription.type, sdp: pc.localDescription.sdp })
      })
      if (!reply.ok) return fail(false, 'signalling refused: ' + reply.status)
      await pc.setRemoteDescription(await reply.json())
    } catch (e) { fail(false, e) }
  }
  // Non-trickle: send once gathering completes — with a settle fallback, because some
  // browsers/sandboxes never report 'complete' (mDNS candidate obfuscation can stall).
  pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') send() }
  pc.onicecandidate = (e) => { if (e.candidate === null) send() }
  setTimeout(send, 1500)
  setTimeout(() => fail(true, ''), $1)
  dc.onopen = succeed
  pc.createOffer().then(o => pc.setLocalDescription(o), e => fail(false, e))
})""")>]
let private openDataChannel (signalUrl: string) (timeoutMs: int) : JS.Promise<{| ok: bool; channel: obj; timedOut: bool; detail: string |}> = jsNative

/// How long a whole handshake gets before it counts as "the session did not answer". Long
/// enough for ICE gathering on a slow machine, short enough that a dead session is reported
/// rather than waited on.
let private channelOpenTimeoutMs = 10000

/// One attempt at the transport, shaped as the resilience policy consumes it.
let private connectChannel (signalUrl: string) : Async<Result<obj, App.ChannelFault>> =
    async {
        let! reply = openDataChannel signalUrl channelOpenTimeoutMs |> Async.AwaitPromise
        return
            if reply.ok then Ok reply.channel
            elif reply.timedOut then Error App.ChannelTimedOut
            else Error (App.ChannelUnreachable reply.detail)
    }

[<Emit("$0.onmessage = (e) => $1(String(e.data))")>]
let private onMessage (dc: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.onclose = $1")>]
let private onClose (dc: obj) (handler: unit -> unit) : unit = jsNative

[<Emit("$0.readyState === 'open' && ($0.send($1), true)")>]
let private sendMessage (dc: obj) (text: string) : bool = jsNative

let private frameCodec : Codec<SessionFrame<string>> = Codec.sessionFrame Codec.string

/// Bridge the push-based browser data channel into the pull-based `FrameChannel`.
let private frameChannel (dc: obj) : FrameChannel<string> =
    let queue = System.Collections.Generic.Queue<SessionFrame<string> option> ()
    let mutable pending : (SessionFrame<string> option -> unit) option = None
    let mutable closed = false
    let deliver item =
        match pending with
        | Some cont -> pending <- None; cont item
        | None -> queue.Enqueue item
    onMessage dc (fun text ->
        match Codec.fromString frameCodec text with
        | Ok frame -> deliver (Some frame)
        | Error e -> JS.console.error ("frame decode failed: " + e))
    onClose dc (fun () ->
        if not closed then
            closed <- true
            deliver None)
    { Send = fun frame -> async { sendMessage dc (Codec.toString frameCodec frame) |> ignore }
      Receive =
        fun () ->
            Async.FromContinuations (fun (cont, _, _) ->
                if queue.Count > 0 then cont (queue.Dequeue ())
                elif closed then cont None
                else pending <- Some cont)
      Close = fun () -> async { emitJsExpr dc "$0.close()" } }

// --- DOM shell -------------------------------------------------------------------------

[<Emit("document.getElementById('app')")>]
let private appRoot () : obj = jsNative

// lit-html's `render` inserts its content AFTER a container's existing children rather
// than replacing them, so the server-rendered shell (first paint) would linger beside the
// live one. Clear it once before the client's first render so Lit owns `#app` outright.
[<Emit("$0.replaceChildren()")>]
let private clearChildren (el: obj) : unit = jsNative

// The timeline is a chat surface: pinned to bottom while the reader is at (or within a few
// px of) the bottom, position preserved when they've scrolled up to read. `-1` marks "was
// pinned". Lit preserves focus/caret across its diff, but scroll is ours to manage.
[<Emit("""(() => {
  const el = document.querySelector('[data-conversation]')
  if (!el) return null
  return el.scrollTop + el.clientHeight >= el.scrollHeight - 4 ? -1 : el.scrollTop
})()""")>]
let private timelineScroll () : float option = jsNative

[<Emit("""(() => {
  const el = document.querySelector('[data-conversation]')
  if (el) el.scrollTop = $0 < 0 ? el.scrollHeight : $0
})()""")>]
let private restoreTimelineScroll (position: float) : unit = jsNative

// A native <input> has no per-character DOM geometry, so we measure the pixel offset of a
// substring with a canvas using the input's own font. Given a peer's decoded selection
// (`anchor`,`head` indices), size its highlight span to `lo..hi` and offset the caret bar to
// `head`. Colour is set by the view (`PeerColour`); this only positions. Called per Title peer
// after every render — the DOM is up to date synchronously.
[<Emit("""(function(peer, a, h){
  const input = document.querySelector('input[data-session-title]')
  const marker = document.querySelector('[data-cursor-peer="' + peer + '"]')
  if (!input || !marker) return
  const cs = getComputedStyle(input)
  const canvas = (window.__yTitleCanvas || (window.__yTitleCanvas = document.createElement('canvas')))
  const ctx = canvas.getContext('2d')
  ctx.font = cs.font && cs.font.trim() ? cs.font : (cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily)
  const value = input.value || ''
  const clamp = (i) => Math.max(0, Math.min(value.length, i | 0))
  const lo = Math.min(clamp(a), clamp(h)), up = Math.max(clamp(a), clamp(h)), head = clamp(h)
  const padLeft = parseFloat(cs.paddingLeft) || 0, scroll = input.scrollLeft || 0
  const xOf = (i) => padLeft + ctx.measureText(value.slice(0, i)).width - scroll
  const loX = xOf(lo)
  marker.style.left = loX + 'px'
  marker.style.width = Math.max(0, xOf(up) - loX) + 'px'
  if (marker.firstElementChild) marker.firstElementChild.style.left = (xOf(head) - loX) + 'px'
})($0, $1, $2)""")>]
let private placeTitleCursor (peer: string) (anchor: int) (head: int) : unit = jsNative

[<Emit("requestAnimationFrame(() => $0())")>]
let private raf (f: unit -> unit) : unit = jsNative

// A trailing debounce: presence decorations are pushed only after the doc stops changing, so a
// decoration transaction never lands while y-prosemirror is applying remote content (which would
// starve the read-only mirrors' rendering of that content — see `pushPresences`).
[<Emit("setTimeout($0, $1)")>]
let private setTimeoutJs (f: unit -> unit) (ms: int) : float = jsNative
[<Emit("clearTimeout($0)")>]
let private clearTimeoutJs (handle: float) : unit = jsNative

// The sidebar/drawer state is one bit on the root element, outside `#app`, so it survives
// every re-render: default = sidebar visible on desktop, off-canvas on mobile; `nav-alt`
// = the inverse (see Style.sidebar).
//
// Collapsing is a PREFERENCE on desktop, so it is remembered; on mobile the same bit means
// "the drawer is open", which is not a preference and is never stored. The stored value is
// re-applied before first paint by the shell document's one inline script (`Ssr.page`) — here,
// only written.
//
// Focus is moved deliberately: the control that was pressed is the one about to disappear, so
// it hands focus to whichever control replaces it (the header's reopen chevron, or the nav
// head's collapse button). Skipping that strands focus on a hidden element.
[<Emit("""(() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  root.classList.toggle('nav-alt')
  // The nav control always returns the column to its workspace face — a column that reopened
  // on settings would be a surprise, and `settings-open` is what chooses the face.
  root.classList.remove('settings-open')
  const shown = desktop !== root.classList.contains('nav-alt')
  if (desktop) { try { localStorage.setItem('yession.nav', shown ? 'open' : 'collapsed') } catch (e) {} }
  requestAnimationFrame(() => {
    const next = document.querySelector(shown ? 'button[data-nav-toggle="hide"]' : '[data-nav-toggle="show"]')
    if (next) next.focus()
  })
})()""")>]
let private toggleNav () : unit = jsNative

// Settings is the sidebar column's other FACE (Style.settingsPane), not a drawer over the
// conversation — so opening it has to bring that column on screen, and `nav-alt` means the
// opposite thing on each side of the breakpoint: uncollapse on desktop, slide the drawer in on
// mobile. Focus follows the same rule as the nav toggle.
[<Emit("""(() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  const opening = !root.classList.contains('settings-open')
  root.classList.toggle('settings-open', opening)
  if (desktop) { if (opening) root.classList.remove('nav-alt') }
  else root.classList.toggle('nav-alt', opening)
  // TWO frames: the face that is arriving is `visibility: hidden` until the transition it
  // just started reaches its first style flush, and `focus()` on a hidden element is a no-op
  // (measured — one frame left focus on <body>).
  requestAnimationFrame(() => requestAnimationFrame(() => {
    const next = document.querySelector(opening ? '[data-settings-toggle="close"]' : '[data-settings-toggle="open"]')
    if (next) next.focus()
  }))
})()""")>]
let private toggleSettings () : unit = jsNative

// The auth probe: `me` answers with a peer token when the browser's cookie (or an
// auth-less session) allows it — total in BOTH axes it can fail on, because the two need
// opposite remedies: `authorized = false` means log in (the shell renavigates), while
// `reachable = false` means the session is not there at all (the shell stays local-first
// on its cached stores and says so). Collapsing them — which a thrown fetch did — turns
// "offline" into "log in", and a login bounce against an unreachable session goes nowhere.
//
// The URL is a PARAMETER, not baked into the Emit: a string literal inside an Emit is
// outside F#'s reach, so a path embedded here could not be checked against
// `SessionRoute`. Every fetch below takes its URL from `SessionRoute.relative`, and the
// browser resolves it against the shell's `<base href>`.
[<Emit("""fetch($0, { cache: 'no-store' }).then(
  r => r.ok ? r.json().then(me => ({ reachable: true, authorized: true, token: me.peerToken, detail: '' }))
            : { reachable: true, authorized: false, token: '', detail: 'HTTP ' + r.status },
  e => ({ reachable: false, authorized: false, token: '', detail: String(e) }))""")>]
let private fetchMe (url: string) : JS.Promise<{| reachable: bool; authorized: bool; token: string; detail: string |}> = jsNative

// `location.assign` resolves against the DOCUMENT's URL, not `<base href>` — the one
// place relative resolution does not follow the base — so resolve explicitly against
// `document.baseURI` here, once, rather than at each call site.
[<Emit("window.location.assign(new URL($0, document.baseURI).href)")>]
let private navigateTo (url: string) : unit = jsNative

// --- Rich-text editor mount ------------------------------------------------------------
// The view renders empty `[data-rich-body="<key>"]` hosts; the editor is mounted imperatively
// into each, bound to the body's live Y.XmlFragment (resolved from the BodyRegistry).
// ProseMirror owns that DOM; Lit leaves the static host's children alone across re-renders
// (as it preserved the textareas).
[<Emit("Array.from(document.querySelectorAll('[data-rich-body]'))")>]
let private richBodyHosts () : obj[] = jsNative

[<Emit("$0.getAttribute('data-rich-body')")>]
let private hostBodyKey (el: obj) : string = jsNative

[<Emit("$0.getAttribute('data-rich-readonly') === 'true'")>]
let private hostReadOnly (el: obj) : bool = jsNative

// --- Client-side doc persistence (Step 20): IndexedDB via y-indexeddb ------------------

[<Import("IndexeddbPersistence", "y-indexeddb")>]
let private indexeddbPersistence : obj = jsNative

[<Emit("new $0($1, $2)")>]
let private newPersistence (ctor: obj) (name: string) (doc: Y.Doc) : obj = jsNative

[<Emit("new Promise((resolve) => $0.once('synced', resolve))")>]
let private whenSynced (persistence: obj) : JS.Promise<unit> = jsNative

// The store is keyed by SESSION: the serving Session Process embeds its session id in the
// bootstrap page (a synchronous, pre-connection identity), so two sessions served from one
// address never share a store, and a session keeps its store wherever it is served from.
[<Emit("""(() => {
  const meta = document.querySelector('meta[name="yession-session"]')
  const session = meta && meta.getAttribute('content')
  return session ? 'yession/session/' + session : 'yession/' + window.location.host + window.location.pathname
})()""")>]
let private persistenceKey () : string = jsNative

/// A `<meta name>`'s content, or None when the tag is absent. `|| null` so a missing tag
/// and a missing attribute both arrive as `None` rather than as `undefined` masquerading
/// as a string.
[<Emit("document.querySelector('meta[name=\"' + $0 + '\"]')?.getAttribute('content') || null")>]
let private metaContent (name: string) : string option = jsNative

// Resolved against the shell's `<base href>`, so a session mounted under a path signals
// to its own prefix rather than the origin root.
[<Emit("new URL($0, document.baseURI).href")>]
let private absolute (relative: string) : string = jsNative

// The event-chunk GET as a TOTAL function: the body, the status it refused with, or the
// transport error it never got past (`status: 0` — offline, refused, DNS, TLS). It never
// rejects, because the information a rejection destroys is exactly the information the
// resilience policy needs to decide whether retrying could help.
[<Emit("""fetch($0).then(
  async r => r.ok ? { ok: true, status: r.status, detail: await r.text() } : { ok: false, status: r.status, detail: '' },
  e => ({ ok: false, status: 0, detail: String(e) }))""")>]
let private fetchChunk (url: string) : JS.Promise<{| ok: bool; status: int; detail: string |}> = jsNative

let private httpGet : App.HttpGet =
    fun url ->
        async {
            let! reply = fetchChunk url |> Async.AwaitPromise
            return
                if reply.ok then Ok reply.detail
                elif reply.status = 0 then Error (App.HttpUnreachable reply.detail)
                else Error (App.HttpStatus reply.status)
        }

[<Emit("Math.random()")>]
let private jsRandom () : float = jsNative

let private mintId (prefix: string) =
    sprintf "%s-%d" prefix (int (jsRandom () * 1000000000.0))

// The peer id is STABLE per browser profile (docs/plans/07): minted once, kept in
// localStorage under a browser-wide key (not per session — it names the browser, the
// same human across sessions), so colours, draft slots, and peer-scoped secrets survive
// reloads. Storage denied (private mode) falls back to the per-load mint.
//
// `$0` is substituted TEXTUALLY, so the argument expression must be bound to a const
// once: with `$0` written three times, the argument (a fresh random mint) evaluated
// three times, and a first visit stored one id while returning a different one. The
// returned id rode the login bounce and was witnessed; the stored id — the one every
// later load reads — was not, so every peer-scoped call (the whole connections surface)
// was denied for the life of the launch.
[<Emit("""(() => {
  const minted = $0
  try {
    const key = 'yession/peer-id'
    const existing = window.localStorage.getItem(key)
    if (existing) return existing
    window.localStorage.setItem(key, minted)
    return minted
  } catch { return minted }
})()""")>]
let private persistentPeerId (minted: string) : string = jsNative

[<Emit("encodeURIComponent($0)")>]
let private urlEncode (value: string) : string = jsNative

// --- Claude connection panel round-trips (Plan 08) --------------------------------------
// Thin fetches against the session's /claude* routes; the same-origin auth cookie rides
// each one. Failures land as `ok: false` with the response text — the panel shows it.

[<Emit("""fetch($0 + '?peer_id=' + encodeURIComponent($1), { cache: 'no-store' })
  .then(r => r.ok ? r.json().then(s => ({ ok: true, session: s.session, mine: s.mine, agent: !!s.agent })) : Promise.resolve({ ok: false, session: null, mine: null, agent: false }))
  .catch(() => ({ ok: false, session: null, mine: null, agent: false }))""")>]
let private fetchClaudeStatusAt (url: string) (peerId: string) : JS.Promise<{| ok: bool; session: string option; mine: string option; agent: bool |}> = jsNative

let private fetchClaudeStatus (peerId: string) =
    fetchClaudeStatusAt (SessionRoute.relative ClaudeStatus) peerId

[<Emit("""fetch($0, { method: 'POST', headers: { 'content-type': 'application/json' }, body: $1 })
  .then(async r => ({ ok: r.ok, body: await r.text() }))
  .catch(e => ({ ok: false, body: String(e) }))""")>]
let private postClaude (url: string) (body: string) : JS.Promise<{| ok: bool; body: string |}> = jsNative

[<Emit("JSON.stringify({ scope: $0, peerId: $1, code: $2 || undefined, token: $3 || undefined })")>]
let private claudeBody (scope: string) (peerId: string) (code: string) (token: string) : string = jsNative

[<Emit("(() => { try { return JSON.parse($0).authorizeUrl || '' } catch { return '' } })()")>]
let private parseAuthorizeUrl (body: string) : string = jsNative

[<Emit("(document.querySelector($0)?.value || '')")>]
let private panelInput (selector: string) : string = jsNative

// --- Entry -----------------------------------------------------------------------------

let private start () =
    async {
        let peerId =
            match PeerId.create (persistentPeerId (mintId "peer")) with
            | Ok id -> id
            | Error e -> failwith e
        let displayName = PeerName.random (Random ())
        let doc = Y.Doc.Create ()
        // Seed from the shell. The session id was already SSR'd into the model
        // (`Signalling.bootstrapHtml`) and then dropped on hydration, so `data-session-id`
        // rendered on the server, blanked, and only came back once `PeerAccepted` landed.
        // Plan 11 makes that load-bearing rather than cosmetic: the reconnect offer names
        // the session to reopen, and it appears precisely when no `PeerAccepted` has
        // happened.
        let initial =
            { ClientModel.init { PeerId = peerId; DisplayName = displayName } with
                Session =
                    metaContent Dom.sessionMetaName
                    |> Option.bind (fun value ->
                        match SessionId.create value with
                        | Ok id -> Some id
                        | Error _ -> None)
                Manager = metaContent Dom.managerMetaName }

        // The connection is wired later (after persistence and signalling); the interrupt
        // control holds this ref so everything else works before — and without — the
        // network (local first). `dispatchRef` lets the connection driver feed inbound
        // frames into the same Elmish loop the view dispatches into.
        let mutable connectionRef : App.Connection option = None
        let mutable dispatchRef : (ClientMsg -> unit) = ignore

        // Rich-text editor mounts. `registry` resolves each body's live Y.XmlFragment (a
        // top-level doc root keyed by BodyKey, so the editor and the Session Process bind the
        // same fragment); `latestModel` lets the mount see the current draft slots. Each mount
        // records the fragment it bound so a fragment swap (a sent draft's slot recreated)
        // triggers a remount.
        let registry = BodyRegistry doc
        let mutable latestModel = initial
        // Keyed by body id; the mount records the fragment AND whether it bound read-only, because
        // both can change under one key: a sent draft's slot is recreated (new fragment), and a
        // draft collapsing to a summary rebinds the same fragment read-only. Either needs a remount
        // — an editable editor left on a collapsed summary would let you type into a one-line
        // preview.
        let mountedBodies = System.Collections.Generic.Dictionary<string, Y.XmlFragment * bool * Editor.EditorHandle> ()
        // The last cursor set pushed into each editor, so a render only dispatches a decoration
        // transaction when it actually changed. Dispatching one every render competes with
        // y-prosemirror's ySync applying REMOTE edits and starves the read-only mirrors' rendering
        // of incoming content — so an unchanged (typically empty) set must never re-dispatch.
        let lastPushed = System.Collections.Generic.Dictionary<string, Editor.RemoteBodyCursor list> ()

        /// The collaborative field a body host names — parsed back from its `BodyKey` so a body
        /// editor's presence report (and the remote cursors pushed into it) carry the right field.
        let fieldOfKey (key: string) : FocusField option =
            if key.StartsWith "draft:" then
                match PeerId.create (key.Substring 6) with
                | Ok p -> Some (DraftBody p)
                | Error _ -> None
            elif key.StartsWith "queue:" then
                match QueueId.create (key.Substring 6) with
                | Ok q -> Some (QueueBody q)
                | Error _ -> None
            else None

        // Presence is reported at most once per animation frame: a caret sweep or drag fires many
        // selection events, but the peer only needs the latest. `latestFocus` is coalesced; the
        // rAF callback ships whatever it is at paint time.
        let mutable focusScheduled = false
        let mutable latestFocus : Focus option = None
        let sendFocus (focus: Focus option) =
            latestFocus <- focus
            if not focusScheduled then
                focusScheduled <- true
                raf (fun () ->
                    focusScheduled <- false
                    connectionRef |> Option.iter (fun c -> c.ReportPresence latestFocus))

        /// Mount an editor on each `[data-rich-body]` host bound to its live fragment; remount when
        /// a host's fragment identity changes; and dispose editors whose host has left the DOM.
        /// Body edits sync through the doc, so the editor needs no change callback — but an editable
        /// body reports its local selection (tagged with the host's field) as rAF-throttled
        /// presence. Mounting publishes no draft slot: the slot follows the body's content
        /// (`DraftSlot.follow` below), so a peer that never types shows no draft box on any peer.
        let syncRichBodies () =
            let seen = System.Collections.Generic.HashSet<string> ()
            for host in richBodyHosts () do
                let key = hostBodyKey host
                seen.Add key |> ignore
                let fragment = registry.Fragment key
                let mount () =
                    let reportFocus (sel: (string * string) option) =
                        match fieldOfKey key, sel with
                        | Some field, Some (a, h) -> sendFocus (Some { Field = field; Pos = { Anchor = a; Head = h } })
                        | _ -> sendFocus None
                    let readOnly = hostReadOnly host
                    mountedBodies.[key] <- (fragment, readOnly, Editor.mountEditor host fragment readOnly reportFocus)
                match mountedBodies.TryGetValue key with
                | true, (bound, readOnly, handle) when
                    not (System.Object.ReferenceEquals (bound, fragment)) || readOnly <> hostReadOnly host ->
                    handle.Dispose (); mount ()
                | true, _ -> ()
                | _ -> mount ()
            for stale in mountedBodies.Keys |> Seq.filter (seen.Contains >> not) |> Seq.toList do
                let _, _, handle = mountedBodies.[stale]
                handle.Dispose ()
                mountedBodies.Remove stale |> ignore
                lastPushed.Remove stale |> ignore

        // The remote cursors currently in a given body, coloured per peer.
        let cursorsFor (key: string) : Editor.RemoteBodyCursor list =
            match fieldOfKey key with
            | Some field ->
                latestModel.Presence
                |> Map.toList
                |> List.filter (fun (_, p) -> p.Focus.Field = field)
                |> List.map (fun (peerId, p) ->
                    ({ Colour = PeerColour.ofPeer peerId
                       Selection = PeerColour.translucent peerId
                       Name = p.DisplayName
                       Anchor = p.Focus.Pos.Anchor
                       Head = p.Focus.Pos.Head } : Editor.RemoteBodyCursor))
            | None -> []

        // Overlay each body's remote cursors, DEBOUNCED: every render (re)arms a short timer, so
        // the decoration dispatch fires only once the model — and thus the doc — has gone quiet.
        // This keeps decoration transactions strictly out of the active-convergence window, where
        // they starve y-prosemirror's rendering of remote content; the caret lands just after the
        // content settles. Only editors whose cursor set changed are dispatched (idle empty→empty
        // is skipped), so a settled editor with no cursors is never disturbed.
        let mutable pushTimer = 0.0
        let pushPresences () =
            clearTimeoutJs pushTimer
            pushTimer <-
                setTimeoutJs (fun () ->
                    for kv in mountedBodies do
                        let key = kv.Key
                        let _, _, handle = kv.Value
                        let cursors = cursorsFor key
                        let prev = match lastPushed.TryGetValue key with | true, v -> v | _ -> []
                        if not (List.isEmpty cursors) || prev <> cursors then
                            lastPushed.[key] <- cursors
                            handle.PushPresences cursors) 150

        /// Place collaborators' title carets by measurement (native inputs have no per-character
        /// geometry): decode each title-focused peer's relative anchor/head against the title
        /// `Y.Text`, then size/offset its marker. A no-op when no remote caret is in the title.
        let placeTitleCursorsAll () =
            for (peerId, p) in Map.toList latestModel.Presence do
                if p.Focus.Field = Title then
                    match ProseMirror.absIndexInDoc doc p.Focus.Pos.Anchor, ProseMirror.absIndexInDoc doc p.Focus.Pos.Head with
                    | Some a, Some h -> placeTitleCursor (PeerId.value peerId) a h
                    | _ -> ()

        // The Claude connection panel's round-trips (Plan 08). Status is polled: once
        // after connect-probe, after every action, and every few seconds while a flow
        // awaits its callback (completion happens in the claude.ai tab, landing at the
        // Manager — this tab learns of it only by asking).
        let refreshClaude () =
            Async.StartImmediate (
                async {
                    let! status = fetchClaudeStatus (PeerId.value peerId) |> Async.AwaitPromise
                    if status.ok then
                        dispatchRef (ClaudeStatusMsg { SessionCredential = status.session; MineCredential = status.mine; AgentAvailable = Some status.agent })
                })
        let rec pollClaudeWhileAwaiting () =
            Async.StartImmediate (
                async {
                    do! Async.Sleep 3000
                    match latestModel.Claude.Flow with
                    | ClaudeAwaitingCode _ ->
                        refreshClaude ()
                        pollClaudeWhileAwaiting ()
                    | _ -> ()
                })
        let claudeAction (run: unit -> Async<Result<string option, string>>) (scope: string) =
            // One shape for every panel action: busy → run → error or refreshed status
            // (and into awaiting-code when the action returned an authorize URL).
            dispatchRef (ClaudeFlowMsg ClaudeBusy)
            Async.StartImmediate (
                async {
                    match! run () with
                    | Error reason -> dispatchRef (ClaudeFlowMsg (ClaudeError reason))
                    | Ok (Some authorizeUrl) ->
                        dispatchRef (ClaudeFlowMsg (ClaudeAwaitingCode (authorizeUrl, scope)))
                        pollClaudeWhileAwaiting ()
                    | Ok None ->
                        dispatchRef (ClaudeFlowMsg ClaudeIdle)
                        refreshClaude ()
                })
        let postClaudeAction (route: string) (scope: string) (code: string) (token: string) (expectUrl: bool) =
            claudeAction
                (fun () ->
                    async {
                        let! reply = postClaude route (claudeBody scope (PeerId.value peerId) code token) |> Async.AwaitPromise
                        if not reply.ok then return Error reply.body
                        elif expectUrl then
                            match parseAuthorizeUrl reply.body with
                            | "" -> return Error "no authorize url in the reply"
                            | url -> return Ok (Some url)
                        else return Ok None
                    })
                scope

        // The side effects a template can't derive from the model. Send routes to the one
        // implementation in `App.connect` (capture markdown, enqueue, seed the queue fragment).
        let actions : ViewActions =
            { SendDraft = fun peer -> connectionRef |> Option.iter (fun c -> c.SendDraft peer)
              DiscardDraft = fun peer -> connectionRef |> Option.iter (fun c -> c.DiscardDraft peer)
              Interrupt = fun turn -> connectionRef |> Option.iter (fun c -> c.InterruptTurn turn)
              ToggleNav = toggleNav
              ToggleSettings =
                fun () ->
                    // Open (or close) the drawer AND re-probe, so it always shows the
                    // current truth the moment it appears.
                    toggleSettings ()
                    refreshClaude ()
              ReportTitleSelection =
                fun sel ->
                    // The title lives in the `title` Y.Text root; turn the input's char offsets
                    // into relative positions over it, so a title caret survives concurrent edits
                    // exactly like a body one. rAF-throttled through the same path as bodies.
                    let focus =
                        sel |> Option.map (fun (anchor, head) ->
                            let title = box (doc.getText "title")
                            let enc i = ProseMirror.relPosFromTypeIndex title i |> ProseMirror.encodeRel
                            { Field = Title; Pos = { Anchor = enc anchor; Head = enc head } })
                    sendFocus focus
              ClaudeConnect =
                fun () ->
                    let scope = match panelInput "[data-claude-scope]" with "" -> "mine" | s -> s
                    postClaudeAction (SessionRoute.relative (Claude ClaudeAction.Begin)) scope "" "" true
              ClaudeComplete =
                fun () ->
                    // The scope selector is unmounted while awaiting; the flow carries it.
                    let scope =
                        match latestModel.Claude.Flow with
                        | ClaudeAwaitingCode (_, scope) -> scope
                        | _ -> "mine"
                    match panelInput "[data-claude-code]" with
                    | "" -> dispatchRef (ClaudeFlowMsg (ClaudeError "paste the code first"))
                    | code -> postClaudeAction (SessionRoute.relative (Claude ClaudeAction.Complete)) scope code "" false
              ClaudePasteToken =
                fun () ->
                    match panelInput "[data-claude-token]" with
                    | "" -> dispatchRef (ClaudeFlowMsg (ClaudeError "paste a token first"))
                    | token ->
                        postClaudeAction
                            (SessionRoute.relative (Claude ClaudeAction.Token))
                            (match panelInput "[data-claude-scope]" with "" -> "mine" | s -> s)
                            ""
                            token
                            false
              ClaudeDisconnect =
                fun scope -> postClaudeAction (SessionRoute.relative (Claude ClaudeAction.Disconnect)) scope "" "" false
              ReopenSession =
                fun () ->
                    // A full navigation to the Manager, not a fetch: it launches the session
                    // if it is stopped and then hands us on to wherever this deployment says
                    // the session lives, which may be a different port entirely. Nothing is
                    // lost by reloading — the doc is in IndexedDB and the log replays — and a
                    // pinned port means we land back on the same origin, so what was written
                    // offline is still here to sync.
                    //
                    // The anchor's href is the same URL, so this is an enhancement rather
                    // than the mechanism: with no JS the link still works.
                    match latestModel.Manager, latestModel.Session with
                    | Some origin, Some sessionId ->
                        navigateTo (sprintf "%s/sessions/%s/open" origin (SessionId.value sessionId))
                    | _ -> () }

        let el = appRoot ()
        // Take over the server-rendered shell (see `clearChildren`): from here Lit owns it.
        clearChildren el

        // Render the Lit view on every model change. Lit diffs into `#app`, so the focused
        // textarea and its caret survive; only the timeline scroll is restored by hand.
        let setState (model: ClientModel) (dispatch: Ylmish.Program.Message<ClientModel, ClientMsg> -> unit) =
            dispatchRef <- fun msg -> dispatch (Ylmish.Program.Message.User msg)
            latestModel <- model
            let scroll = timelineScroll ()
            Lit.render (unbox el) (View.view actions model dispatchRef)
            match scroll with
            | Some position -> restoreTimelineScroll position
            | None -> ()
            // Mount/dispose the rich editors on their body hosts (bound to live fragments), then
            // overlay collaborators' cursors: remote carets in each body editor, and title carets
            // measured against the just-rendered input.
            syncRichBodies ()
            pushPresences ()
            placeTitleCursorsAll ()

        App.makeProgram doc initial
        |> Program.withSetState setState
        |> Program.run

        // The local peer's draft slot follows its body: published on the first keystroke,
        // retracted when the composer empties. Watches the body itself, so a keystroke and a
        // merged remote edit settle it and nothing else does.
        DraftSlot.follow doc registry peerId (fun msg -> dispatchRef msg) |> ignore

        // Local-first: the doc persists in IndexedDB keyed by the session's address. Cold
        // loads render local state (drafts, queued messages) before — and without — the
        // network; on reconnect the full-state exchange reconciles.
        let persistence = newPersistence indexeddbPersistence (persistenceKey ()) doc
        do! whenSynced persistence |> Async.AwaitPromise

        // The replayed doc is state that did not arrive as a body change, so settle the rule
        // against it explicitly: a doc stored before publication followed the body can hold an
        // empty-bodied slot, and this is where it goes.
        DraftSlot.settle doc registry peerId (fun msg -> dispatchRef msg)

        // Authorization by renavigation: probe `/me` for a peer token. 401 -> bounce
        // through `/login` (code + PKCE via the Manager) and land back on this shell,
        // where the probe succeeds. A NETWORK failure (offline, session down) is a
        // `Disconnected` with its reason, not silence: the local-first shell — IndexedDB doc
        // plus cached event chunks — stays fully usable, and the model says why it is alone.
        let! probe = fetchMe (SessionRoute.relative Me) |> Async.AwaitPromise
        if not probe.reachable then
            dispatchRef (ConnectFailedMsg (App.ChannelFault.describe (App.ChannelUnreachable probe.detail)))
        elif not probe.authorized then
            // The peer id rides the login bounce so the Manager can witness which peer
            // signed in for this session (docs/plans/07 — peer-scoped secrets).
            navigateTo (SessionRoute.relative Login + "?peer_id=" + urlEncode (PeerId.value peerId))
        else
            // Authenticated: the Claude panel's status is knowable now.
            refreshClaude ()
            let hello =
                { PeerId = peerId
                  DisplayName = displayName
                  Token = probe.token }
            // Events come over HTTP in immutable chunks, so the browser's own cache serves
            // history; only the growing tail chunk hits the Session Process. Availability hints
            // still arrive over the data channel. The same-origin auth cookie rides each
            // fetch, so no token in the URL (history/cache stay clean).
            //
            // Both resilience policies are composed HERE, at the transport, and nowhere else:
            // `App.connect` is handed a feed that has already spent its retries, so the read
            // loop only ever sees a settled outcome and the application code holds no notion of
            // retrying, backoff, or attempt counts. Interim progress is the policy's to report,
            // which is the one thing a settled outcome cannot carry.
            let feed =
                App.EventFetch.overHttp httpGet SessionRoute.relative None
                |> Resilience.Policy.guard
                    (App.EventFetch.policy Resilience.Policy.sleep jsRandom (fun attempt ->
                        App.EventFetch.retrying attempt
                        |> Option.iter (fun health -> dispatchRef (EventFeedMsg health))))
            let options = { App.ConnectOptions.defaults with FetchEvents = Some feed }
            let openChannel =
                Resilience.Policy.guard
                    (App.SessionChannel.policy Resilience.Policy.sleep jsRandom)
                    (fun () -> connectChannel (absolute (SessionRoute.relative Signal)))

            // The session leg. The RULES — announce, open, serve, and come back only for a
            // session that was accepted — are `App.SessionLifecycle`; this supplies the
            // browser's four ports and nothing else.
            do!
                App.SessionLifecycle.run
                    { Open = openChannel
                      Serve =
                        fun resumeAfter dispatch dc ->
                            async {
                                let connection =
                                    App.connect
                                        { options with ResumeAfter = resumeAfter }
                                        doc
                                        registry
                                        hello
                                        dispatch
                                        (frameChannel dc)
                                connectionRef <- Some connection
                                do! connection.Run
                                connectionRef <- None
                            }
                      ReadPosition = fun () -> latestModel.EventConsumer.LastProcessedOffset
                      Dispatch = fun msg -> dispatchRef msg }
    }

Async.StartImmediate (start ())
