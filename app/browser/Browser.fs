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

        // Rich-text editor mounts. `registry` resolves each body's live Y.XmlFragment (a
        // top-level doc root keyed by BodyKey, so the editor and the Session Process bind the
        // same fragment); `latestModel` lets the mount see the current draft slots. Each mount
        // records the fragment it bound so a fragment swap (a sent draft's slot recreated)
        // triggers a remount.
        let registry = BodyRegistry doc
        let mutable latestModel = initial
        let mountedBodies = System.Collections.Generic.Dictionary<string, Y.XmlFragment * Editor.EditorHandle> ()

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

        /// Mount an editor on each `[data-rich-body]` host bound to its live fragment; ensure the
        /// local draft slot exists (so its fragment anchors); remount when a host's fragment
        /// identity changes; and dispose editors whose host has left the DOM. Body edits sync
        /// through the doc, so the editor needs no change callback — but an editable body reports
        /// its local selection (tagged with the host's field) as rAF-throttled presence.
        let syncRichBodies () =
            let seen = System.Collections.Generic.HashSet<string> ()
            for host in richBodyHosts () do
                let key = hostBodyKey host
                seen.Add key |> ignore
                if key = BodyKey.draft peerId && not (Map.containsKey peerId latestModel.Synced.Drafts) then
                    dispatchRef (EnsureDraftMsg peerId)
                let fragment = registry.Fragment key
                let mount () =
                    let reportFocus (sel: (string * string) option) =
                        match fieldOfKey key, sel with
                        | Some field, Some (a, h) -> sendFocus (Some { Field = field; Pos = { Anchor = a; Head = h } })
                        | _ -> sendFocus None
                    mountedBodies.[key] <- (fragment, Editor.mountEditor host fragment (hostReadOnly host) reportFocus)
                match mountedBodies.TryGetValue key with
                | true, (bound, handle) when not (System.Object.ReferenceEquals (bound, fragment)) ->
                    handle.Dispose (); mount ()
                | true, _ -> ()
                | _ -> mount ()
            for stale in mountedBodies.Keys |> Seq.filter (seen.Contains >> not) |> Seq.toList do
                (snd mountedBodies.[stale]).Dispose ()
                mountedBodies.Remove stale |> ignore

        /// Push each mounted body's remote cursors into its editor: the peers whose focus is in
        /// that body, coloured per peer (`PeerColour`), positioned by their relative anchor/head.
        let pushPresences () =
            for kv in mountedBodies do
                let handle = snd kv.Value
                let cursors =
                    match fieldOfKey kv.Key with
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
                handle.PushPresences cursors

        /// Place collaborators' title carets by measurement (native inputs have no per-character
        /// geometry): decode each title-focused peer's relative anchor/head against the title
        /// `Y.Text`, then size/offset its marker. A no-op when no remote caret is in the title.
        let placeTitleCursorsAll () =
            for (peerId, p) in Map.toList latestModel.Presence do
                if p.Focus.Field = Title then
                    match ProseMirror.absIndexInDoc doc p.Focus.Pos.Anchor, ProseMirror.absIndexInDoc doc p.Focus.Pos.Head with
                    | Some a, Some h -> placeTitleCursor (PeerId.value peerId) a h
                    | _ -> ()

        // The side effects a template can't derive from the model. Send routes to the one
        // implementation in `App.connect` (capture markdown, enqueue, seed the queue fragment).
        let actions : ViewActions =
            { SendDraft = fun peer -> connectionRef |> Option.iter (fun c -> c.SendDraft peer)
              Interrupt = fun turn -> connectionRef |> Option.iter (fun c -> c.InterruptTurn turn)
              ToggleNav = toggleNav
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
                    sendFocus focus }

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
        let connection = App.connect options doc registry hello (fun msg -> dispatchRef msg) channel
        connectionRef <- Some connection

        do! connection.Run
    }

Async.StartImmediate (start ())
