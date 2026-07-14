module Yession.Browser.Main

// The browser client entry: the same Elmish/Ylmish program as everywhere else, wired to
// the Session Process over a *native* WebRTC data channel (the Node tests use
// libdatachannel; the protocol and signalling are identical). The DOM shell renders the
// pure `View` on every model change and delegates the interactive controls back into
// dispatch — the markup stays the single source of truth.

open System
open Elmish
open Fable.Core
open Fable.Core.JsInterop
open Yjs
open Yession.Domain
open Yession.Client

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

[<Emit("$0.innerHTML = $1")>]
let private setHtml (el: obj) (html: string) : unit = jsNative

[<Emit("""(() => {
  const el = document.activeElement
  if (!el || !el.getAttribute) return null
  const draft = el.getAttribute('data-draft-input')
  if (draft) return 'data-draft-input:' + draft
  const queued = el.getAttribute('data-queue-input')
  if (queued) return 'data-queue-input:' + queued
  return null
})()""")>]
let private focusedEditor () : string option = jsNative

[<Emit("""(() => {
  const idx = $0.indexOf(':')
  const el = document.querySelector(`[${$0.slice(0, idx)}="${$0.slice(idx + 1)}"]`)
  if (el) { el.focus(); el.setSelectionRange(el.value.length, el.value.length) }
})()""")>]
let private refocusEditor (key: string) : unit = jsNative

[<Emit("""$0.addEventListener($1, (e) => {
  const target = e.target.closest ? e.target.closest(`[${$2}]`) : null
  if (target) $3(target.getAttribute($2) || '', target.value !== undefined ? String(target.value) : '')
})""")>]
let private delegate' (el: obj) (eventName: string) (attribute: string) (handler: string -> string -> unit) : unit = jsNative

[<Emit("new URLSearchParams(window.location.search).get($0) || $1")>]
let private queryParam (name: string) (fallback: string) : string = jsNative

// --- Client-side doc persistence (Step 20): IndexedDB via y-indexeddb ------------------

[<Import("IndexeddbPersistence", "y-indexeddb")>]
let private indexeddbPersistence : obj = jsNative

[<Emit("new $0($1, $2)")>]
let private newPersistence (ctor: obj) (name: string) (doc: Y.Doc) : obj = jsNative

[<Emit("new Promise((resolve) => $0.once('synced', resolve))")>]
let private whenSynced (persistence: obj) : JS.Promise<unit> = jsNative

[<Emit("'yession/' + window.location.host + window.location.pathname")>]
let private persistenceKey () : string = jsNative

[<Emit("String(window.location.origin) + '/signal'")>]
let private signalUrl () : string = jsNative

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

        let root = appRoot ()
        let mutable dispatchRef : (ClientMsg -> unit) = ignore
        let mutable currentModel = initial

        // Render the pure view on every model change. If a draft or queue textarea is
        // focused, its element is re-created by the render — restore focus and caret so
        // typing is uninterrupted (the model already holds the text being typed).
        let setState (model: ClientModel) (dispatch: Ylmish.Program.Message<ClientModel, ClientMsg> -> unit) =
            currentModel <- model
            dispatchRef <- fun msg -> dispatch (Ylmish.Program.Message.User msg)
            let focused = focusedEditor ()
            setHtml root (View.render model)
            match focused with
            | Some key when not (isNull (box key)) -> refocusEditor key
            | _ -> ()

        App.makeProgram doc initial
        |> Program.withSetState setState
        |> Program.run

        // Local-first: the doc persists in IndexedDB keyed by the session's address.
        // Cold loads render local state (drafts, queued messages) before — and without
        // — the network; on reconnect the full-state exchange reconciles, and entries
        // the Process consumed meanwhile meet its removal tombstones and converge.
        let persistence = newPersistence indexeddbPersistence (persistenceKey ()) doc
        do! whenSynced persistence |> Async.AwaitPromise

        let! dc = openDataChannel (signalUrl ()) |> Async.AwaitPromise
        let channel = frameChannel dc
        let hello =
            { PeerId = peerId
              DisplayName = displayName
              Token = queryParam "token" "local-dev-token" }
        let connection = App.connect App.ConnectOptions.defaults doc hello (fun msg -> dispatchRef msg) channel

        // Interactive controls, delegated so re-renders never lose listeners.
        delegate' root "click" "data-start-draft" (fun _ _ ->
            match DraftId.create (mintId "draft") with
            | Ok draftId -> dispatchRef (StartDraftMsg draftId)
            | Error _ -> ())
        delegate' root "click" "data-send-draft" (fun draftId _ ->
            match DraftId.create draftId with
            | Ok id -> connection.SendDraft id
            | Error _ -> ())
        delegate' root "input" "data-draft-input" (fun draftId value ->
            match DraftId.create draftId with
            | Ok id ->
                // Derive the edit intent against the CURRENT body, so the splice merges
                // instead of clobbering concurrent edits.
                match Map.tryFind id currentModel.Synced.Drafts with
                | Some draft -> dispatchRef (EditDraftBodyMsg (id, Ylmish.Text.edit value draft.Body))
                | None -> ()
            | Error _ -> ())

        // Queue controls (Phase 3): queued messages stay editable, reorderable, and
        // deletable by any peer until the agent takes them.
        delegate' root "input" "data-queue-input" (fun queueId value ->
            match QueueId.create queueId with
            | Ok id ->
                match Map.tryFind id currentModel.Synced.Queue with
                | Some entry -> dispatchRef (EditQueuedBodyMsg (id, Ylmish.Text.edit value entry.Body))
                | None -> ()
            | Error _ -> ())
        delegate' root "click" "data-queue-delete" (fun queueId _ ->
            match QueueId.create queueId with
            | Ok id -> dispatchRef (DeleteQueuedMsg id)
            | Error _ -> ())
        delegate' root "click" "data-queue-up" (fun queueId _ ->
            match QueueId.create queueId with
            | Ok id ->
                match QueueOrder.moveUp currentModel.Synced.Queue id with
                | Some order -> dispatchRef (ReorderQueuedMsg (id, order))
                | None -> ()
            | Error _ -> ())
        delegate' root "click" "data-queue-down" (fun queueId _ ->
            match QueueId.create queueId with
            | Ok id ->
                match QueueOrder.moveDown currentModel.Synced.Queue id with
                | Some order -> dispatchRef (ReorderQueuedMsg (id, order))
                | None -> ()
            | Error _ -> ())
        delegate' root "click" "data-interrupt-turn" (fun turnId _ ->
            match AgentTurnId.create turnId with
            | Ok id -> connection.InterruptTurn id
            | Error _ -> ())

        do! connection.Run
    }

Async.StartImmediate (start ())
