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

[<Emit("""new Promise((resolve) => {
  const pc = new RTCPeerConnection({ iceServers: [] })
  const dc = pc.createDataChannel('session')
  let sent = false
  const send = async () => {
    if (sent) return
    sent = true
    const answer = await fetch($0, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ type: pc.localDescription.type, sdp: pc.localDescription.sdp })
    }).then(r => r.json())
    await pc.setRemoteDescription(answer)
  }
  // Non-trickle: send once gathering completes — with a settle fallback, because some
  // browsers/sandboxes never report 'complete' (mDNS candidate obfuscation can stall).
  pc.onicegatheringstatechange = () => { if (pc.iceGatheringState === 'complete') send() }
  pc.onicecandidate = (e) => { if (e.candidate === null) send() }
  setTimeout(send, 1500)
  dc.onopen = () => resolve(dc)
  pc.createOffer().then(o => pc.setLocalDescription(o))
})""")>]
let private openDataChannel (signalUrl: string) : JS.Promise<obj> = jsNative

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

// Remote cursors carry a character index; a native <input> has no per-character DOM
// geometry, so we measure the pixel offset of the substring before the caret with a canvas
// using the input's own font, then place each caret absolutely (and colour it by a stable
// hash of the peer id). Runs after every Lit render — the DOM is up to date synchronously.
[<Emit("""(() => {
  const input = document.querySelector('input[data-session-title]')
  const markers = document.querySelectorAll('[data-title-cursor]')
  if (!input || !markers.length) return
  const cs = getComputedStyle(input)
  const canvas = (window.__yTitleCanvas || (window.__yTitleCanvas = document.createElement('canvas')))
  const ctx = canvas.getContext('2d')
  ctx.font = cs.font && cs.font.trim() ? cs.font : (cs.fontStyle + ' ' + cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily)
  const value = input.value || ''
  const padLeft = parseFloat(cs.paddingLeft) || 0
  for (const m of markers) {
    const raw = parseInt(m.getAttribute('data-title-cursor'), 10)
    const idx = Math.max(0, Math.min(value.length, isNaN(raw) ? 0 : raw))
    const w = ctx.measureText(value.slice(0, idx)).width
    m.style.left = (padLeft + w - (input.scrollLeft || 0)) + 'px'
    const peer = m.getAttribute('data-cursor-peer') || ''
    let h = 0; for (let i = 0; i < peer.length; i++) h = (h * 31 + peer.charCodeAt(i)) | 0
    const color = 'hsl(' + ((((h % 360) + 360) % 360)) + ', 70%, 55%)'
    m.style.background = color
    if (m.firstElementChild) m.firstElementChild.style.background = color
  }
})()""")>]
let private positionTitleCursors () : unit = jsNative

// The sidebar/drawer state is one bit on the root element, outside `#app`, so it survives
// every re-render: default = sidebar visible on desktop, off-canvas on mobile; `nav-alt`
// = the inverse (see Style.sidebar).
[<Emit("document.documentElement.classList.toggle('nav-alt')")>]
let private toggleNav () : unit = jsNative

[<Emit("new URLSearchParams(window.location.search).get($0) || $1")>]
let private queryParam (name: string) (fallback: string) : string = jsNative

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

[<Emit("String(window.location.origin) + '/signal'")>]
let private signalUrl () : string = jsNative

[<Emit("fetch($0).then(r => { if (!r.ok) throw new Error('events fetch failed: ' + r.status); return r.text() })")>]
let private fetchText (url: string) : JS.Promise<string> = jsNative

[<Emit("Math.random()")>]
let private jsRandom () : float = jsNative

let private mintId (prefix: string) =
    sprintf "%s-%d" prefix (int (jsRandom () * 1000000000.0))

// --- Entry -----------------------------------------------------------------------------

let private start () =
    async {
        let peerId =
            match PeerId.create (mintId "peer") with
            | Ok id -> id
            | Error e -> failwith e
        let displayName = PeerName.random (Random ())
        let doc = Y.Doc.Create ()
        let initial = ClientModel.init { PeerId = peerId; DisplayName = displayName }

        // The connection is wired later (after persistence and signalling); the interrupt
        // control holds this ref so everything else works before — and without — the
        // network (local first). `dispatchRef` lets the connection driver feed inbound
        // frames into the same Elmish loop the view dispatches into.
        let mutable connectionRef : App.Connection option = None
        let mutable dispatchRef : (ClientMsg -> unit) = ignore

        // Rich-text editor mounts. `registry` supplies each body's live Y.XmlFragment (shared
        // with the codec, so the editor and the sync boundary bind the same nested fragment);
        // `latestModel` lets the mount see the current draft slots.
        let registry = BodyRegistry ()
        let mutable latestModel = initial
        let mountedBodies = System.Collections.Generic.Dictionary<string, unit -> unit> ()
        // Markdown captured at send, applied to the new queue body fragment once it anchors.
        let pendingSeeds = System.Collections.Generic.Dictionary<string, string> ()

        /// Mount an editor on each `[data-rich-body]` host bound to its live fragment; ensure the
        /// local draft slot exists (so its fragment anchors); apply pending send-seeds; and
        /// dispose editors whose host has left the DOM. Body edits sync through the doc, so the
        /// editor needs no change callback.
        let syncRichBodies () =
            let seen = System.Collections.Generic.HashSet<string> ()
            for host in richBodyHosts () do
                let key = hostBodyKey host
                seen.Add key |> ignore
                if key = BodyKey.draft peerId && not (Map.containsKey peerId latestModel.Synced.Drafts) then
                    dispatchRef (EnsureDraftMsg peerId)
                match registry.TryFragment key with
                | Some fragment ->
                    (match pendingSeeds.TryGetValue key with
                     | true, md ->
                         if md <> "" then Markdown.intoFragment md fragment
                         pendingSeeds.Remove key |> ignore
                     | _ -> ())
                    if not (mountedBodies.ContainsKey key) then
                        mountedBodies.[key] <- Editor.mountEditor host fragment (hostReadOnly host)
                | None -> ()
            for stale in mountedBodies.Keys |> Seq.filter (seen.Contains >> not) |> Seq.toList do
                mountedBodies.[stale] ()
                mountedBodies.Remove stale |> ignore

        /// Send the local draft: capture its body as markdown, dispose its editor (the slot's
        /// fragment is about to be removed), enqueue, and seed the new queue entry's fragment
        /// when it anchors on a following render.
        let sendDraft (peer: PeerId) =
            match QueueId.create (mintId "queue") with
            | Ok queueId ->
                let md =
                    registry.TryFragment (BodyKey.draft peer)
                    |> Option.map Markdown.ofFragment
                    |> Option.defaultValue ""
                (match mountedBodies.TryGetValue (BodyKey.draft peer) with
                 | true, dispose -> dispose (); mountedBodies.Remove (BodyKey.draft peer) |> ignore
                 | _ -> ())
                pendingSeeds.[BodyKey.queued queueId] <- md
                dispatchRef (SendDraftMsg (peer, queueId))
            | Error e -> failwith e

        // The side effects a template can't derive from the model.
        let actions : ViewActions =
            { SendDraft = sendDraft
              Interrupt = fun turn -> connectionRef |> Option.iter (fun c -> c.InterruptTurn turn)
              ToggleNav = toggleNav
              ReportTitleCursor = fun index -> connectionRef |> Option.iter (fun c -> c.ReportCursor index) }

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
            // Place collaborators' title carets by measurement (native inputs have no
            // per-character geometry); a no-op when there are no remote cursors.
            positionTitleCursors ()
            // Mount/dispose the rich editors on their body hosts (bound to live fragments).
            syncRichBodies ()

        App.makeProgram doc registry initial
        |> Program.withSetState setState
        |> Program.run

        // Local-first: the doc persists in IndexedDB keyed by the session's address. Cold
        // loads render local state (drafts, queued messages) before — and without — the
        // network; on reconnect the full-state exchange reconciles.
        let persistence = newPersistence indexeddbPersistence (persistenceKey ()) doc
        do! whenSynced persistence |> Async.AwaitPromise

        let! dc = openDataChannel (signalUrl ()) |> Async.AwaitPromise
        let channel = frameChannel dc
        let token = queryParam "token" "local-dev-token"
        let hello =
            { PeerId = peerId
              DisplayName = displayName
              Token = token }
        // Events come over HTTP in immutable chunks, so the browser's own cache serves
        // history; only the growing tail chunk hits the Session Process. Availability hints
        // still arrive over the data channel.
        let options =
            { App.ConnectOptions.defaults with
                FetchEvents = Some (App.EventFetch.overHttp (fetchText >> Async.AwaitPromise) "" token) }
        let connection = App.connect options doc hello (fun msg -> dispatchRef msg) channel
        connectionRef <- Some connection

        do! connection.Run
    }

Async.StartImmediate (start ())
