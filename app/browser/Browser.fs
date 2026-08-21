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
[<Emit("""(function (signalUrl, timeoutMs) { return (
new Promise((resolve) => {
  const t0 = performance.now()
  const took = () => Math.round(performance.now() - t0)
  const pc = new RTCPeerConnection({ iceServers: [] })
  const dc = pc.createDataChannel('session')
  let settled = false
  const succeed = () => { if (!settled) { settled = true; resolve({ ok: true, channel: dc, connection: pc, timedOut: false, detail: '', tookMs: took() }) } }
  const fail = (timedOut, detail) => {
    if (settled) return
    settled = true
    try { pc.close() } catch {}
    resolve({ ok: false, channel: null, connection: null, timedOut, detail: String(detail), tookMs: took() })
  }
  let sent = false
  const send = async () => {
    if (sent || settled) return
    sent = true
    try {
      const reply = await fetch(signalUrl, {
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
  setTimeout(() => fail(true, ''), timeoutMs)
  dc.onopen = succeed
  pc.createOffer().then(o => pc.setLocalDescription(o), e => fail(false, e))
})
) })($0, $1)""")>]
let private openDataChannel (signalUrl: string) (timeoutMs: int) : JS.Promise<{| ok: bool; channel: obj; connection: obj; timedOut: bool; detail: string; tookMs: int |}> = jsNative

/// How long a whole handshake gets before it counts as "the session did not answer". Long
/// enough for ICE gathering on a slow machine, short enough that a dead session is reported
/// rather than waited on.
let private channelOpenTimeoutMs = 10000

[<Emit("$0.onmessage = (e) => $1(String(e.data))")>]
let private onMessage (dc: obj) (handler: string -> unit) : unit = jsNative

[<Emit("$0.onclose = $1")>]
let private onClose (dc: obj) (handler: unit -> unit) : unit = jsNative

[<Emit("(function (dc, text) { return dc.readyState === 'open' && (dc.send(text), true) })($0, $1)")>]
let private sendMessage (dc: obj) (text: string) : bool = jsNative

/// Both of the peer connection's state machines, as one "this transport is finished" signal.
///
/// This is why the connection is kept at all. The promise above used to resolve with the data
/// channel ALONE, so nothing could observe either state, nothing could close a dead connection,
/// and the only way a client learned its transport had died was `dc.onclose` — an event a
/// half-open channel never fires.
///
/// `disconnected` is deliberately NOT here. It is a maybe, not a verdict, and the honest answer
/// to a maybe already exists: the heartbeat asks, and gets an answer or does not, inside about
/// three seconds. A grace timer here would be a second clock measuring the same doubt.
//
// The local names here are deliberately NOT `pc`/`dc`: `$0` is substituted TEXTUALLY with the
// caller's identifier, so `const pc = $0` at a call site whose argument is itself named `pc`
// emits `const pc = pc` — a temporal dead zone error that takes the whole shell down at load.
[<Emit("""(function (pc, handler) {
  const peer = pc, onDead = handler
  const finished = () =>
    peer.connectionState === 'failed' || peer.connectionState === 'closed' ||
    peer.iceConnectionState === 'failed' || peer.iceConnectionState === 'closed'
  const check = () => { if (finished()) onDead() }
  peer.addEventListener('connectionstatechange', check)
  peer.addEventListener('iceconnectionstatechange', check)
  check()
})($0, $1)""")>]
let private onPeerFinished (pc: obj) (handler: unit -> unit) : unit = jsNative

/// Look again the moment the page comes back — a phone returning from the background, a
/// network coming back, a tab being switched to. Returns the way to stop looking.
///
/// Not a second mechanism: it asks exactly the question `onPeerFinished` answers, at the one
/// moment a browser is most likely to have torn the transport down while no script was running
/// to hear about it. That moment is where the reported bug lived.
[<Emit("""(function (pc, dc, handler) {
  const peer = pc, chan = dc, onDead = handler
  const look = () => {
    if (document.visibilityState === 'hidden') return
    if (peer.connectionState === 'failed' || peer.connectionState === 'closed' ||
        peer.iceConnectionState === 'failed' || peer.iceConnectionState === 'closed' ||
        chan.readyState !== 'open') onDead()
  }
  window.addEventListener('pageshow', look)
  window.addEventListener('online', look)
  document.addEventListener('visibilitychange', look)
  return () => {
    window.removeEventListener('pageshow', look)
    window.removeEventListener('online', look)
    document.removeEventListener('visibilitychange', look)
  }
})($0, $1, $2)""")>]
let private onResume (pc: obj) (dc: obj) (handler: unit -> unit) : (unit -> unit) = jsNative

[<Emit("$0.close()")>]
let private closePeer (pc: obj) : unit = jsNative

let private frameCodec : Codec<SessionFrame<string>> = Codec.sessionFrame Codec.string

/// Bridge the push-based browser data channel into the pull-based `FrameChannel`, and hold the
/// peer connection that carries it for as long as it lasts.
///
/// The channel ends exactly once, however the news arrives — the data channel closing, either
/// state machine reaching a terminal state, or a resumed page finding the transport already
/// gone. Three triggers, one mechanism: end of stream. Whoever is pumping learns it the way
/// they always did.
///
/// Closing closes the CONNECTION too. It used to close only the channel, which left a peer
/// connection (and its ICE agent) alive behind every reconnect for the life of the page.
let private frameChannel (dc: obj) (pc: obj) : FrameChannel<string> =
    let queue = System.Collections.Generic.Queue<SessionFrame<string> option> ()
    let mutable pending : (SessionFrame<string> option -> unit) option = None
    let mutable closed = false
    let mutable stopLooking : unit -> unit = ignore
    let deliver item =
        match pending with
        | Some cont -> pending <- None; cont item
        | None -> queue.Enqueue item
    let finish () =
        if not closed then
            closed <- true
            stopLooking ()
            deliver None
    onMessage dc (fun text ->
        match Codec.fromString frameCodec text with
        | Ok frame -> deliver (Some frame)
        | Error e -> JS.console.error ("frame decode failed: " + e))
    onClose dc finish
    onPeerFinished pc finish
    stopLooking <- onResume pc dc finish
    { Send = fun frame -> async { sendMessage dc (Codec.toString frameCodec frame) |> ignore }
      Receive =
        fun () ->
            Async.FromContinuations (fun (cont, _, _) ->
                if queue.Count > 0 then cont (queue.Dequeue ())
                elif closed then cont None
                else pending <- Some cont)
      Close =
        fun () ->
            async {
                finish ()
                emitJsExpr dc "$0.close()"
                closePeer pc
            } }

/// One attempt at the transport, shaped as the resilience policy consumes it. What settles is
/// a CHANNEL, not the WebRTC objects behind it: the peer connection never leaves this module,
/// which is what lets everything above hold one idea of a transport.
let private connectChannel (signalUrl: string) : Async<Result<FrameChannel<string>, App.ChannelFault>> =
    async {
        let! reply = openDataChannel signalUrl channelOpenTimeoutMs |> Async.AwaitPromise
        // How long the handshake took, said out loud. Open latency is a property this repo has
        // already traded a whole ICE backend to protect (docs/decisions/2026-07-26), and it is
        // invisible from the outside: a slow session and a slow handshake look identical from
        // the shell. Free on success, and the one number worth having when they do not.
        JS.console.debug (
            sprintf "yession/link: handshake %s in %dms" (if reply.ok then "opened" else "failed") reply.tookMs)
        return
            if reply.ok then Ok (frameChannel reply.channel reply.connection)
            elif reply.timedOut then Error App.ChannelTimedOut
            else Error (App.ChannelUnreachable reply.detail)
    }

// --- DOM shell -------------------------------------------------------------------------

[<Emit("document.getElementById('app')")>]
let private appRoot () : obj = jsNative

// lit-html's `render` inserts its content AFTER a container's existing children rather
// than replacing them, so the server-rendered shell (first paint) would linger beside the
// live one. Clear it once before the client's first render so Lit owns `#app` outright.
[<Emit("$0.replaceChildren()")>]
let private clearChildren (el: obj) : unit = jsNative

// The two surfaces that are read from their END — the chat, and a terminal's scrollback.
// Both are pinned to the bottom while the reader is at (or within a few px of) it, and both
// keep their place when they have scrolled up to read. `-1` marks "was pinned". Lit
// preserves focus/caret across its diff, but scroll is ours to manage.
//
// One selector list, taken once and put back once: the terminal used to have neither half,
// so a command whose output arrived after the render left the newest line below the fold
// with nothing to say it was there.
let [<Literal>] private PinnedSurfaces = "[data-conversation],[data-terminal-scrollback]"

// Keyed by what the surface IS, never by its position in the list: a terminal that took its
// lease between two renders removes its scrollback from the document, and an index would
// then put its scroll position into the chat.
[<Emit("""(function (selector) {
  const key = el => el.getAttribute('data-terminal-id') || 'chat'
  const taken = {}
  for (const el of document.querySelectorAll(selector)) {
    taken[key(el)] = el.scrollTop + el.clientHeight >= el.scrollHeight - 4 ? -1 : el.scrollTop
  }
  return taken
})($0)""")>]
let private surfaceScroll (selector: string) : obj = jsNative

// A surface that was NOT on screen before this render starts at its end, which is the other
// half of "content grows from the top and the viewport rides the tail": opening a terminal
// with a history behind it, or switching to one, should show the newest lines and not the
// oldest. It used to fall through to `scrollTop = 0` — invisible while the stream hugged the
// bottom of a short box with `mt-auto`, and plainly wrong the moment the history was longer
// than the box, which is exactly when the anchoring stopped applying.
[<Emit("""(function (selector, positions) {
  const key = el => el.getAttribute('data-terminal-id') || 'chat'
  for (const el of document.querySelectorAll(selector)) {
    const position = positions[key(el)]
    el.scrollTop = position === undefined || position < 0 ? el.scrollHeight : position
  }
})($0, $1)""")>]
let private restoreSurfaceScroll (selector: string) (positions: obj) : unit = jsNative

// A RENDER is not the only thing that moves the end of one of those surfaces away from the
// reader — a RESIZE does it too, and on a phone the viewport is not a constant: the
// browser's toolbars come and go, the device turns. The shell is the visible viewport's
// height (`Style.app`), so each of those shortens the timeline's box while its `scrollTop`
// stays exactly where it was, and somebody who was at the end of the conversation is left a
// line and a half short of it — the last thing said, cut in half, just above the composer.
//
// Whether they were at the end has to be sampled BEFORE the box changes (by the time the
// resize handler runs the measurement would always say "no"), so it rides the scroll event —
// captured, because scroll does not bubble, and the element is Lit's to replace.
[<Emit("""(function (selector) {
  const sel = selector
  const atEnd = el => el.scrollTop + el.clientHeight >= el.scrollHeight - 4
  const pinned = new WeakMap()
  document.addEventListener('scroll', e => {
    const el = e.target
    if (el instanceof Element && el.matches(sel)) pinned.set(el, atEnd(el))
  }, true)
  window.addEventListener('resize', () => {
    for (const el of document.querySelectorAll(sel)) {
      if (pinned.get(el) !== false) el.scrollTop = el.scrollHeight
    }
  })
})($0)""")>]
let private keepSurfacesPinned (selector: string) : unit = jsNative

// A native <input> has no per-character DOM geometry, so we measure the pixel offset of a
// substring with a canvas using the input's own font. Given a peer's decoded selection
// (`anchor`,`head` indices), size its highlight span to `lo..hi` and offset the caret bar to
// `head`. Colour is set by the view (`PeerColour`); this only positions. Called per Title peer
// after every render — the DOM is up to date synchronously.
//
// Everything the marker needs is READ OFF THE FIELD, never assumed from the stylesheet: the
// marker is a sibling of the input inside the title block, and where the input's text sits in
// that block is a function of the input's own offset, padding and content box. The title is a
// 28/32 heading at one width and a 19/24 pivot at the other, and its padding is spent outward
// so a fill can appear without moving a glyph — a marker placed from constants would be right
// at exactly one of those and silently wrong at the rest.
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
  const px = (v) => parseFloat(v) || 0
  const padLeft = px(cs.paddingLeft), padTop = px(cs.paddingTop), scroll = input.scrollLeft || 0
  const left = input.offsetLeft + px(cs.borderLeftWidth) + padLeft
  const top = input.offsetTop + px(cs.borderTopWidth) + padTop
  const height = input.clientHeight - padTop - px(cs.paddingBottom)
  const xOf = (i) => left + ctx.measureText(value.slice(0, i)).width - scroll
  const loX = xOf(lo)
  marker.style.left = loX + 'px'
  marker.style.top = top + 'px'
  marker.style.height = height + 'px'
  marker.style.width = Math.max(0, xOf(up) - loX) + 'px'
  if (marker.firstElementChild) marker.firstElementChild.style.left = (xOf(head) - loX) + 'px'
})($0, $1, $2)""")>]
let private placeTitleCursor (peer: string) (anchor: int) (head: int) : unit = jsNative

[<Emit("requestAnimationFrame(() => $0())")>]
let private raf (f: unit -> unit) : unit = jsNative

// An armed deadline: something is true NOW and only worth saying if it is still true then
// (see `syncCatchUpTimer`). Nothing debounces on it any more — what needs pacing is paced by
// the frame (`raf`).
[<Emit("setTimeout($0, $1)")>]
let private setTimeoutJs (f: unit -> unit) (ms: int) : float = jsNative
[<Emit("clearTimeout($0)")>]
let private clearTimeoutJs (handle: float) : unit = jsNative

/// How long catch-up must run before it is worth SAYING (see `EventConsumerState.CatchUpIsSlow`).
/// Long enough that a send — which puts this client one event behind itself for a round trip —
/// never lights it; short enough that a real wait is reported rather than sat through in
/// silence.
let private catchUpQuietMs = 500

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

// The same move, in one direction only.
//
// A call to action that leads to settings must never TAKE somebody there and back: the
// prompt over the timeline is on screen whenever a credential needs signing in, including
// while the settings face is already open, and a toggle there would shut the very panel it
// is pointing at. The nav pivots stay toggles because a pivot is a two-way control and this
// is not one.
//
// Idempotent by construction rather than by the caller checking first — `settings-open` is
// SET, not flipped, so pressing it twice is pressing it once.
[<Emit("""(() => {
  const root = document.documentElement
  const desktop = window.matchMedia('(min-width: 768px)').matches
  const wasOpen = root.classList.contains('settings-open')
  root.classList.add('settings-open')
  // Bring the column on screen: `nav-alt` means the opposite thing on each side of the
  // breakpoint — collapsed on desktop, drawer-open on mobile.
  if (desktop) root.classList.remove('nav-alt')
  else root.classList.add('nav-alt')
  // Focus moves only when the face actually ARRIVED. Stealing it from whatever the reader
  // was doing, to a control that was already on screen, would be the prompt reaching into a
  // panel they are already reading.
  if (!wasOpen) {
    // TWO frames, for the reason the toggle needs them: the arriving face is
    // `visibility: hidden` until the transition it just started reaches its first style
    // flush, and `focus()` on a hidden element is a no-op.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const next = document.querySelector('[data-settings-toggle="close"]')
      if (next) next.focus()
    }))
  }
})()""")>]
let private revealSettings () : unit = jsNative

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
//
// A REFUSAL is 401/403 and nothing else. Every other error status — a 502 from the
// operator's proxy standing in front of a session that is gone, a 503 from one still
// starting — is the session not being there, which is the other axis entirely. Reading them
// as "log in" sent a client whose session had stopped off to a login bounce that could only
// fail, and (once the shell was served from a worker) replaced a perfectly good offline
// session with a browser error page. The thrown case was already right; this is the same
// distinction for the answers that arrive.
[<Emit("""fetch($0, { cache: 'no-store' }).then(
  r => r.ok ? r.json().then(me => ({ reachable: true, authorized: true, token: me.peerToken, detail: '' }))
      : (r.status === 401 || r.status === 403)
        ? { reachable: true, authorized: false, token: '', detail: 'HTTP ' + r.status }
        : { reachable: false, authorized: false, token: '', detail: 'HTTP ' + r.status },
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

// --- Terminal command lines (Plan 13) --------------------------------------------------
// The view renders `<input data-terminal-input="<key>">` for each terminal composer slot and
// each queued command; the value is bound imperatively to that key's `Y.Text` root, the same
// arrangement the rich bodies use one level up. An `<input>` rather than an editor because a
// command is characters, and the CRDT merge happens per character either way.

[<Emit("Array.from(document.querySelectorAll('[data-terminal-input]'))")>]
let private terminalInputs () : obj[] = jsNative

[<Emit("$0.childElementCount > 0")>]
let private isMounted (el: obj) : bool = jsNative

[<Emit("$0.getAttribute('data-terminal-input')")>]
let private terminalInputKey (el: obj) : string = jsNative

[<Emit("$0.readOnly === true")>]
let private terminalInputReadOnly (el: obj) : bool = jsNative

[<Emit("$0.value")>]
let private inputValue (el: obj) : string = jsNative

/// Set an input's value while keeping the caret where the person left it. A remote edit
/// re-renders the value under a focused input, and `el.value = …` resets the selection to
/// the end — which is a collaborator's keystroke throwing your cursor across the line.
/// Offsets are clamped, so a shorter value cannot leave the caret past the end.
// The locals are `__y`-prefixed for a reason that cost an afternoon: Fable substitutes
// `$0` with the ARGUMENT'S OWN IDENTIFIER, so a template that declares `const el = $0`
// against an F# value also called `el` emits `let el = el` — a temporal-dead-zone
// self-reference that throws at the first call. Names that no F# binding will ever have
// make the substitution safe whatever the call site is called.
[<Emit("""(function (el, value) {
  const __yInput = el, __yNext = value;
  if (__yInput.value === __yNext) return;
  const __yFocused = document.activeElement === __yInput;
  const __yStart = __yFocused ? __yInput.selectionStart : null;
  const __yEnd = __yFocused ? __yInput.selectionEnd : null;
  __yInput.value = __yNext;
  if (__yFocused && __yStart !== null) {
    const __yLimit = __yNext.length;
    __yInput.setSelectionRange(Math.min(__yStart, __yLimit), Math.min(__yEnd, __yLimit));
  }
})($0, $1)""")>]
let private setInputValue (el: obj) (value: string) : unit = jsNative

/// Attach a listener once. The flag lives on the element, so a Lit re-render that reuses the
/// same element does not stack a second handler on it — and one that creates a fresh element
/// gets its own.
///
/// Because it is once, the handlers passed here must decide from the ELEMENT what they are
/// acting on: an input Lit hands to a second terminal is this same element with a new
/// `data-terminal-input`, and these listeners are the ones it keeps.
///
/// Enter RUNS the command, the same bargain the message composer strikes (`Editor`'s keymap).
/// A command line is one line, so there is no new line for Alt-Enter to insert and none is
/// bound. `isComposing` guards the IME: mid-composition Enter commits the candidate word, and
/// running a half-typed command because someone accepted a suggestion is not a thing to do.
[<Emit("""(function (el, onInput, onSelect, onBlur, onEnter) {
  const __yBind = el;
  if (__yBind.__yessionBound) return false;
  __yBind.__yessionBound = true;
  __yBind.addEventListener('input', onInput);
  __yBind.addEventListener('keyup', onSelect);
  __yBind.addEventListener('click', onSelect);
  __yBind.addEventListener('select', onSelect);
  __yBind.addEventListener('focus', onSelect);
  __yBind.addEventListener('blur', onBlur);
  __yBind.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); onEnter() }
  });
  return true;
})($0, $1, $2, $3, $4)""")>]
let private bindTerminalInput
    (el: obj)
    (onInput: unit -> unit)
    (onSelect: unit -> unit)
    (onBlur: unit -> unit)
    (onEnter: unit -> unit)
    : bool = jsNative

[<Emit("(function (el) { return (el && typeof el.selectionStart === 'number') ? [el.selectionStart, el.selectionEnd] : null })($0)")>]
let private inputSelection (el: obj) : (int * int) option = jsNative

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
// `r.url` is the address the answer came back FROM, which after a redirect is not the one
// that was asked for — and it is the one worth keeping, because a range's bounds never move
// while a cursor's answer does (Plan 20).
[<Emit("""fetch($0).then(
  async r => r.ok ? { ok: true, status: r.status, url: r.url, detail: await r.text() } : { ok: false, status: r.status, url: r.url, detail: '' },
  e => ({ ok: false, status: 0, url: '', detail: String(e) }))""")>]
let private fetchChunk (url: string) : JS.Promise<{| ok: bool; status: int; url: string; detail: string |}> = jsNative

let private httpGet : App.HttpGet =
    fun url ->
        async {
            let! reply = fetchChunk url |> Async.AwaitPromise
            return
                if reply.ok then Ok { Url = reply.url; Body = reply.detail }
                elif reply.status = 0 then Error (App.HttpUnreachable reply.detail)
                else Error (App.HttpStatus reply.status)
        }

// --- The history store (Plan 20, step 2): the Cache API, not the HTTP cache ---------------
// A cache header can say "reuse this without asking me"; it cannot say "keep this". The HTTP
// cache is a bounded pool shared with every other site, reclaimed under pressure and wiped by
// the browsing-data checkbox people tick casually — and it can be neither enumerated nor asked
// to persist. The Cache API is all three, and it needs no service worker: `window.caches` is
// reachable from the page in any secure context.
//
// Whole `Response` objects, keyed by the address they came back from — which after the
// cursor's redirect is the range, whose bounds never move. That is what makes an answer
// keepable, and the client never has to construct one.

[<Emit("(typeof window !== 'undefined' && window.isSecureContext === true && !!window.caches)")>]
let private canKeepHistory () : bool = jsNative

// The cache is named for the SESSION, which closes inside the client what a URL-keyed cache
// could not: the zero-config deployment addresses sessions as `127.0.0.1:{port}` and ports are
// recycled, so a shared key would hand one session the previous one's history. Derived from the
// doc store's key rather than spelled again — one rule for what identifies a session's storage.
let private historyCacheName () = persistenceKey () + "/events"

[<Emit("window.caches.open($0)")>]
let private openCache (name: string) : JS.Promise<obj> = jsNative

// `keys()` answers in insertion order, and insertion order is NOT log order: `put` of an
// address already kept deletes the entry and appends the new one, so an answer two tabs both
// fetched moves to the end of the enumeration. The replay orders by what the answers hold
// (`App.EventFetch.replay`); this is a bag of addresses and promises nothing about their order.
[<Emit("$0.keys().then(rs => rs.map(r => r.url))")>]
let private cacheKeys (cache: obj) : JS.Promise<string array> = jsNative

[<Emit("$0.match($1).then(r => r ? r.text() : null)")>]
let private cacheRead (cache: obj) (url: string) : JS.Promise<string option> = jsNative

// A FRESH Response, never the one that came off the network: a response carrying
// `redirected = true` is a known trap in the Cache API, and re-wrapping also keeps the store
// free of anything about how the bytes were obtained.
[<Emit("$0.put($1, new Response($2, { headers: { 'content-type': 'application/x-ndjson; charset=utf-8' } })).catch(() => undefined)")>]
let private cacheWrite (cache: obj) (url: string) (body: string) : JS.Promise<unit> = jsNative

/// Register the worker that makes a cold open possible with no network (Plan 20).
///
/// Best-effort and deliberately unawaited-for-correctness: a client whose registration fails
/// (an insecure context, a browser that refuses) is exactly today's client — it just cannot
/// open cold. Nothing above this waits on it, and nothing breaks if it never resolves.
/// Returns `unit`, and that is load-bearing rather than stylistic. As a promise-returning
/// emit whose result was discarded (`|> ignore`), the whole call was dead code to the
/// compiler and never reached the bundle at all — the registration silently did not ship,
/// which looks exactly like a worker that will not take control. A unit-returning emit is a
/// statement, and statements survive.
[<Emit("""void (navigator.serviceWorker && navigator.serviceWorker.register($0).catch(() => undefined))""")>]
let private registerWorker (url: string) : unit = jsNative

/// Ask for the store to be kept. A request, not a guarantee — granted for an engaged site on
/// Chrome, essentially only for an installed app on Safari — and best-effort by design: the
/// answer changes nothing this client does, it only changes how long what it kept survives.
[<Emit("(navigator.storage && navigator.storage.persist) ? navigator.storage.persist().catch(() => false) : Promise.resolve(false)")>]
let private requestPersistence () : JS.Promise<bool> = jsNative

/// The history store for this session, or the one that keeps nothing when this context cannot
/// have one. Total either way: a client with no store is exactly today's client, asking the
/// network from its cursor.
let private openHistoryCache () : Async<App.HistoryCache> =
    async {
        if not (canKeepHistory ()) then return App.HistoryCache.none
        else
            let! cache = openCache (historyCacheName ()) |> Async.AwaitPromise
            let! _ = requestPersistence () |> Async.AwaitPromise
            return
                { Stored = fun () -> async { let! keys = cacheKeys cache |> Async.AwaitPromise in return List.ofArray keys }
                  Read = fun url -> cacheRead cache url |> Async.AwaitPromise
                  Write = fun url body -> cacheWrite cache url body |> Async.AwaitPromise }
    }

// --- One store per terminal (Plan 22) -----------------------------------------------------
// Same Cache API, one cache per terminal, so a replay can walk one terminal's answers without
// asking whose each entry is. The terminal's id is in the cache's NAME, which is what makes
// that question already answered when the walk starts — and what keeps a hole in one
// terminal's history from stopping another's.

let private transcriptCachePrefix () = persistenceKey () + "/terminals/"

let private transcriptCacheName (terminal: TerminalId) = transcriptCachePrefix () + TerminalId.value terminal

[<Emit("window.caches.keys()")>]
let private cacheNames () : JS.Promise<string array> = jsNative

// The line an answer starts on, kept BESIDE the bytes rather than parsed back out of the
// address: a transcript line cannot carry its own index, and the address is the one thing this
// client is never allowed to read meaning out of. It rides a header on the stored `Response`,
// which the Cache API round-trips for nothing.
[<Emit("""$0.put($1, new Response($3, { headers: { 'content-type': 'application/x-ndjson; charset=utf-8', 'x-yession-first-seq': String($2) } })).catch(() => undefined)""")>]
let private transcriptWrite (cache: obj) (url: string) (firstSeq: int) (body: string) : JS.Promise<unit> = jsNative

// `null` for an entry that is gone, and for one written without the header — which no build
// that shipped this ever wrote, but a store outlives the build that filled it.
[<Emit("""(function (cache, url) { return (
cache.match(url).then(async r => {
  if (!r) return null
  const first = r.headers.get('x-yession-first-seq')
  if (first === null) return null
  return [parseInt(first, 10), await r.text()]
})
) })($0, $1)""")>]
let private transcriptRead (cache: obj) (url: string) : JS.Promise<(int * string) option> = jsNative

/// Every terminal's store for this session, or the one that keeps nothing.
let private openTranscriptCaches () : Async<App.TranscriptCaches> =
    async {
        if not (canKeepHistory ()) then return App.TranscriptCaches.none
        else
            return
                { For =
                    fun terminal ->
                        async {
                            // Opening is idempotent and cheap, and doing it per call is what
                            // keeps this a lookup rather than a registry something has to
                            // remember to populate before a terminal is first written to.
                            let! cache = openCache (transcriptCacheName terminal) |> Async.AwaitPromise
                            return
                                { Stored =
                                    fun () ->
                                        async {
                                            let! keys = cacheKeys cache |> Async.AwaitPromise
                                            return List.ofArray keys
                                        }
                                  Read = fun url -> transcriptRead cache url |> Async.AwaitPromise
                                  Write =
                                    fun url first body -> transcriptWrite cache url first body |> Async.AwaitPromise }
                        }
                  Kept =
                    fun () ->
                        async {
                            let! names = cacheNames () |> Async.AwaitPromise
                            let prefix = transcriptCachePrefix ()
                            return
                                names
                                |> Array.filter (fun name -> name.StartsWith prefix)
                                |> Array.map (fun name -> name.Substring prefix.Length)
                                // An id the store held but this build cannot parse is a
                                // terminal this client cannot fold records for anyway.
                                |> Array.choose (fun id ->
                                    match TerminalId.create id with
                                    | Ok terminal -> Some terminal
                                    | Error _ -> None)
                                |> List.ofArray
                        } }
    }

// --- Waiting to try again (Plan 20, step 3) ----------------------------------------------
// ONE wait, poked by two triggers. "Keep trying", "the network came back" and "someone pressed
// retry" are three ways to want the same thing, and building them as three schedules would put
// three of them in a race. So: the lifecycle decides WHETHER and HOW LONG to wait, and these
// only ever cut a wait short.
//
// `ms < 0` is the park a refused peer gets — no timer at all, because no amount of waiting
// fixes a token. It ends when the network returns or a person asks, which are the two things
// that can.

/// Cut the current wait short, if one is running. Replaced each time a wait begins.
let mutable private pokeRetry : unit -> unit = ignore

[<Emit("""(function (ms, register) { return (
new Promise(resolve => {
  let settled = false
  const finish = () => {
    if (settled) return
    settled = true
    window.removeEventListener('online', finish)
    if (timer !== null) clearTimeout(timer)
    resolve(true)
  }
  const timer = ms >= 0 ? setTimeout(finish, ms) : null
  window.addEventListener('online', finish)
  register(finish)
})
) })($0, $1)""")>]
let private waitOrPoke (ms: float) (register: (unit -> unit) -> unit) : JS.Promise<bool> = jsNative

let private waitBeforeRetry (delay: System.TimeSpan option) : Async<bool> =
    async {
        let ms =
            match delay with
            | Some d -> d.TotalMilliseconds
            | None -> -1.0
        return!
            waitOrPoke ms (fun finish -> pokeRetry <- finish)
            |> Async.AwaitPromise
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
[<Emit("""(function (minted) {
  try {
    const key = 'yession/peer-id'
    const existing = window.localStorage.getItem(key)
    if (existing) return existing
    window.localStorage.setItem(key, minted)
    return minted
  } catch { return minted }
})($0)""")>]
let private persistentPeerId (minted: string) : string = jsNative

[<Emit("encodeURIComponent($0)")>]
let private urlEncode (value: string) : string = jsNative

// --- Claude connection panel round-trips (Plan 08) --------------------------------------
// Thin fetches against the session's /claude* routes; the same-origin auth cookie rides
// each one, and IS the whole identity — the browser asserts nothing about who it is.
// Failures land as `ok: false` with the response text — the panel shows it.
//
// These used to carry the peer id, and the credential was owned by it. A peer id lives in
// origin-partitioned localStorage, so it changed under the person holding it and stranded
// the credential behind every new one; ownership now comes off the cookie, Manager-side.

// A connection arrives as `{kind, signInRequired}` or null, and is flattened to primitives
// HERE rather than carried across as an object. Fable's mapping of an option-of-record onto
// a JS value is the kind of thing that misbehaves quietly, and a status that silently
// decodes to "nothing connected" is indistinguishable on screen from the truth. Two nullable
// strings per scope cannot go wrong, and `ConnectionView` is assembled in F#.
[<Emit("""fetch($0, { cache: 'no-store' })
  .then(r => r.ok ? r.json().then(s => ({ ok: true,
    sessionKind: s.session ? String(s.session.kind || '') : null,
    sessionSignIn: (s.session && s.session.signInRequired) || null,
    mineKind: s.mine ? String(s.mine.kind || '') : null,
    mineSignIn: (s.mine && s.mine.signInRequired) || null,
    owner: s.owner, agent: !!s.agent }))
    : Promise.resolve({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null, owner: null, agent: false }))
  .catch(() => ({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null, owner: null, agent: false }))""")>]
let private fetchClaudeStatusAt (url: string) : JS.Promise<{| ok: bool; sessionKind: string option; sessionSignIn: string option; mineKind: string option; mineSignIn: string option; owner: string option; agent: bool |}> = jsNative

/// One scope's pair of nullable strings, as the panel's row reads it.
let private viewOf (kind: string option) (signInRequired: string option) : ConnectionView option =
    kind |> Option.map (fun kind -> { Kind = kind; SignInRequired = signInRequired })

let private fetchClaudeStatus () =
    fetchClaudeStatusAt (SessionRoute.relative ClaudeStatus)

[<Emit("""fetch($0, { method: 'POST', headers: { 'content-type': 'application/json' }, body: $1 })
  .then(async r => ({ ok: r.ok, body: await r.text() }))
  .catch(e => ({ ok: false, body: String(e) }))""")>]
let private postClaude (url: string) (body: string) : JS.Promise<{| ok: bool; body: string |}> = jsNative

[<Emit("JSON.stringify({ scope: $0, code: $1 || undefined, token: $2 || undefined })")>]
let private claudeBody (scope: string) (code: string) (token: string) : string = jsNative

[<Emit("(() => { try { return JSON.parse($0).authorizeUrl || '' } catch { return '' } })()")>]
let private parseAuthorizeUrl (body: string) : string = jsNative

[<Emit("(document.querySelector($0)?.value || '')")>]
let private panelInput (selector: string) : string = jsNative

// --- GitHub connection panel round-trips (Plan 14) ---------------------------------------
// Same fetch shapes as the Claude panel's; the flow differs (device code) so the two
// extra parsers below read the begin/poll replies.

[<Emit("""fetch($0, { cache: 'no-store' })
  .then(r => r.ok ? r.json().then(s => ({ ok: true,
    sessionKind: s.session ? String(s.session.kind || '') : null,
    sessionSignIn: (s.session && s.session.signInRequired) || null,
    mineKind: s.mine ? String(s.mine.kind || '') : null,
    mineSignIn: (s.mine && s.mine.signInRequired) || null }))
    : Promise.resolve({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null }))
  .catch(() => ({ ok: false, sessionKind: null, sessionSignIn: null, mineKind: null, mineSignIn: null }))""")>]
let private fetchGitHubStatusAt (url: string) : JS.Promise<{| ok: bool; sessionKind: string option; sessionSignIn: string option; mineKind: string option; mineSignIn: string option |}> = jsNative

let private fetchGitHubStatus () =
    fetchGitHubStatusAt (SessionRoute.relative GitHubStatus)

[<Emit("JSON.stringify({ scope: $0, token: $1 || undefined })")>]
let private githubBody (scope: string) (token: string) : string = jsNative

[<Emit("(function (body) { try { const o = JSON.parse(body); return { userCode: o.userCode || '', verificationUri: o.verificationUri || '', interval: o.interval || 5 } } catch { return { userCode: '', verificationUri: '', interval: 5 } } })($0)")>]
let private parseDeviceBegin (body: string) : {| userCode: string; verificationUri: string; interval: int |} = jsNative

[<Emit("(function (body) { try { const o = JSON.parse(body); return { status: o.status || '', interval: o.interval || 0 } } catch { return { status: '', interval: 0 } } })($0)")>]
let private parseDevicePoll (body: string) : {| status: string; interval: int |} = jsNative

// --- The model catalogue (the picker's supply) -------------------------------------------
// One gated fetch, decoded with the shared codec — the reply IS `AgentModel list`, and the
// browser never learns which provider produced it. A non-2xx carries the reason as text,
// which is the whole error story: the picker shows it and the provider default still works.

[<Emit("""fetch($0, { cache: 'no-store' })
  .then(async r => ({ ok: r.ok, body: await r.text() }))
  .catch(e => ({ ok: false, body: String(e) }))""")>]
let private fetchModelsAt (url: string) : JS.Promise<{| ok: bool; body: string |}> = jsNative

// --- The read surface's stream (Plan 15) --------------------------------------------------
// `EventSource` rather than the repo's fetch-based SSE reader: it is the browser's own SSE
// client, it reconnects on its own, and it carries the session cookie same-origin — which
// is the whole authentication story for a route that is cookie-gated.
//
// ONE connection carries every query. It is opened once at start and never closed: there
// is nothing to re-probe on, because a value arrives when it changes rather than when
// somebody looks.

[<Emit("(function (url, onFrame) { const es = new EventSource(url); es.onmessage = e => onFrame(e.data); return es })($0, $1)")>]
let private openQueryStream (url: string) (onFrame: string -> unit) : obj = jsNative

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
                Manager = metaContent Dom.managerMetaName
                EphemeralStorage = (metaContent Dom.ephemeralStorageMetaName).IsSome
                // The one place that can answer this: the model defaults to true because the
                // SERVER renders this shell too and has no idea what the browser can do.
                CanKeepHistory = canKeepHistory () }

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
        // The plain-text roots the terminal composers live in (Plan 13), beside the rich
        // bodies and resolved the same way.
        let texts = TextRegistry doc
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
            elif key.StartsWith "term-draft:" then
                // `term-draft:<terminal>:<peer>` — split on the FIRST colon after the
                // prefix, because a terminal id is Crockford base32 and never contains one.
                let rest = key.Substring 11
                let idx = rest.IndexOf ':'
                if idx <= 0 then None
                else
                    match TerminalId.create (rest.Substring (0, idx)), PeerId.create (rest.Substring (idx + 1)) with
                    | Ok terminal, Ok author -> Some (TerminalDraftBody (terminal, author))
                    | _ -> None
            elif key.StartsWith "term-queue:" then
                match QueueId.create (key.Substring 11) with
                | Ok q -> Some (TerminalQueuedBody q)
                | Error _ -> None
            else None

        // Presence is reported at most once per animation frame: a caret sweep or drag fires many
        // selection events, but the peer only needs the latest. `latestFocus` is coalesced; the
        // rAF callback ships whatever it is at paint time — and only if it differs from what was
        // last shipped.
        //
        // The dedup lives HERE, with the one presence slot it governs, rather than in each
        // reporter. Three of them share that slot — the rich editor's plugin, the title input,
        // and terminal command lines — and a reporter that compared against its OWN last value
        // would be answering a question about somebody else's write: a command line re-reporting
        // `None` after the editor had claimed the caret would suppress a clear that was needed.
        // Compared on the encoded focus, which is precisely what goes on the wire, so a report is
        // dropped only when it would tell a collaborator nothing.
        //
        // Safe because presence is relayed live and last-write-wins (`Host.broadcastPresenceExcept`)
        // — no TTL, so a repeat is never a keepalive. It costs a stationary caret nothing that the
        // reporters were not already costing it: the editor plugin has always dropped an unmoved
        // selection, so a peer arriving late has never been shown one.
        let mutable focusScheduled = false
        let mutable latestFocus : Focus option = None
        let mutable sentFocus : Focus option option = None
        let sendFocus (focus: Focus option) =
            latestFocus <- focus
            if not focusScheduled then
                focusScheduled <- true
                raf (fun () ->
                    focusScheduled <- false
                    if sentFocus <> Some latestFocus then
                        sentFocus <- Some latestFocus
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
                    // Enter sends — but only from a DRAFT, which is the only body with a send.
                    // A queued message is edited in place and has nothing to commit, so it
                    // keeps plain Enter (and Alt-Enter never has to be learned there).
                    let onSubmit =
                        match fieldOfKey key with
                        | Some (DraftBody author) ->
                            Some (fun () -> connectionRef |> Option.iter (fun c -> c.SendDraft author))
                        | _ -> None
                    let readOnly = hostReadOnly host
                    // A prompt where there is a message to write — the same body the send
                    // binding above belongs to. A queued message being edited already holds
                    // words, and someone else's draft is not yours to be invited into.
                    let placeholder =
                        match fieldOfKey key with
                        | Some (DraftBody _) -> Dom.Text.composerPlaceholder
                        | _ -> ""
                    mountedBodies.[key] <-
                        (fragment, readOnly, Editor.mountEditor host fragment readOnly reportFocus onSubmit placeholder)
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

        /// Bind every rendered terminal command line to its `Y.Text` root: push the CRDT's
        /// value in, send the input's edits back out as the MINIMUM edit that gets there
        /// (`TerminalText.setTo` — anything coarser would clobber a collaborator rather than
        /// merge with them), and report the caret as presence.
        ///
        /// Called after every render AND on every doc update, because a terminal command
        /// line is a root the Ylmish codec does not carry (it holds only the slot's
        /// identity), so a remote keystroke in one does not necessarily reach the model.
        let syncTerminalInputs () =
            for el in terminalInputs () do
                // WHICH line an input is, and whether it may be written to, are read off the
                // element every time a handler runs — never captured when it was bound.
                //
                // Lit reuses one `<input>` across a tab switch (same template, same position,
                // a different terminal's key) and the handlers are attached once per element,
                // so a captured key outlives the terminal it named: keystrokes went into the
                // terminal the input was FIRST rendered for while its value was pushed from
                // the one it now shows, which wiped the line being typed into on every render
                // and left the command in the other terminal, last character only. Same for
                // read-only: a collaborator's slot and your own composer are the same
                // position in that template, so "bind only the editable one" bound whichever
                // it was first and got the other wrong ever after.
                let lineOf () =
                    let key = terminalInputKey el
                    if isNull (box key) || key = "" then None
                    // A read-only line (a collaborator's slot) still shows live text; it just
                    // never writes back, and never claims a caret.
                    elif terminalInputReadOnly el then None
                    else Some key
                // Four events report the caret here and most report it unmoved — a keyup for
                // every key that types rather than navigates, a click landing where the caret
                // already was, the focus that precedes both. They are dropped by `sendFocus`,
                // which is where the slot they all write to lives.
                let reportFocus () =
                    match lineOf () with
                    | Some key ->
                        match fieldOfKey key, inputSelection el with
                        | Some field, Some (anchor, head) ->
                            let root = box (texts.Text key)
                            let enc i = ProseMirror.relPosFromTypeIndex root i |> ProseMirror.encodeRel
                            sendFocus (Some { Field = field; Pos = { Anchor = enc anchor; Head = enc head } })
                        | _ -> sendFocus None
                    | None -> sendFocus None
                // Enter runs a command from a composer SLOT — the line you are writing.
                // A queued command's line has already been sent; Enter there does
                // nothing rather than queueing it twice.
                let onEnter () =
                    match lineOf () |> Option.bind fieldOfKey with
                    | Some (TerminalDraftBody (terminal, author)) ->
                        connectionRef |> Option.iter (fun c -> c.SendTerminalDraft terminal author)
                    | _ -> ()
                bindTerminalInput
                    el
                    (fun () -> lineOf () |> Option.iter (fun key -> TerminalText.setTo texts key (inputValue el)))
                    reportFocus
                    (fun () -> sendFocus None)
                    onEnter
                |> ignore
                let key = terminalInputKey el
                if not (isNull (box key)) && key <> "" then setInputValue el (TerminalText.read texts key)

        /// Fetch the keyframes the open tabs need, once each (Plan 14, stage 4). A keyframe
        /// is immutable at a position that never moves, so the browser cache serves the
        /// second read — this set only stops a burst of identical in-flight requests while
        /// the first one is still out.
        let keyframesAsked = System.Collections.Generic.HashSet<string> ()

        let syncKeyframes (model: ClientModel) =
            for tab in ClientModel.paneTabs model do
                match ClientModel.missingKeyframe tab model with
                | None -> ()
                | Some (terminal, seq) ->
                    let key = sprintf "%s@%d" (TerminalId.value terminal) seq
                    if keyframesAsked.Add key then
                        Async.StartImmediate (
                            async {
                                let url = SessionRoute.relative (TerminalKeyframe (TerminalId.value terminal, seq))
                                match! httpGet url with
                                // A keyframe that does not answer is not a failure: the range
                                // still rebases and still plays, as the naive slice. Asking
                                // again on every render would be a spin with nothing to gain.
                                | Error _ -> ()
                                | Ok answer ->
                                    match Codec.fromString Codec.transcriptKeyframe answer.Body with
                                    | Ok keyframe -> dispatchRef (TerminalKeyframeMsg (terminal, keyframe))
                                    | Error _ -> ()
                            })

        let replays = PaneReplays.create (fun msg -> dispatchRef msg)

        /// The live screens (Plan 14, stage 6): one emulator per terminal this client has a
        /// snapshot for, folded forward from the records the model already holds.
        let screens = Screens.create (fun msg -> dispatchRef msg)

        /// The size each terminal's viewport was last reported at, so a re-render does not
        /// re-send a size nothing changed — a resize is a signal to the program on the other
        /// end, and repeating it makes a full-screen program redraw for no reason.
        let reportedSize = System.Collections.Generic.Dictionary<string, int * int> ()

        /// Tell the Session Process how big the holder's screen is. Only the HOLDER: the pty
        /// has one size and every peer is looking at the same screen, so a viewer with a
        /// narrower pane must scroll rather than reshape everyone else's terminal.
        let syncScreenSize (model: ClientModel) =
            let mine = ActorRef.PeerRef model.Peer.PeerId
            for terminal in TerminalProjection.openTerminals model.Terminals do
                if terminal.Lease = Some mine then
                    let key = TerminalId.value terminal.TerminalId
                    match Screens.measure key with
                    | None -> ()
                    | Some (cols, rows) ->
                        let last = match reportedSize.TryGetValue key with | true, v -> Some v | _ -> None
                        if last <> Some (cols, rows) then
                            reportedSize.[key] <- (cols, rows)
                            connectionRef |> Option.iter (fun c -> c.ResizeTerminal terminal.TerminalId cols rows)

        // The publication rule, one subscription per open terminal. Started when a terminal
        // appears and stopped when it goes, so a closed terminal's rule cannot republish a
        // slot into a terminal that no longer exists.
        let terminalSlots = System.Collections.Generic.Dictionary<string, Subscription> ()

        let syncTerminalSlots (model: ClientModel) =
            let openIds =
                TerminalProjection.openTerminals model.Terminals |> List.map (fun t -> TerminalId.value t.TerminalId)
            for terminal in TerminalProjection.openTerminals model.Terminals do
                let key = TerminalId.value terminal.TerminalId
                if not (terminalSlots.ContainsKey key) then
                    terminalSlots.[key] <-
                        TerminalDraftSlot.follow doc texts terminal.TerminalId peerId (fun msg -> dispatchRef msg)
            for stale in terminalSlots.Keys |> Seq.filter (fun k -> not (List.contains k openIds)) |> Seq.toList do
                terminalSlots.[stale].Stop ()
                terminalSlots.Remove stale |> ignore

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

        // Catch-up is the normal state for a moment after anything happens — your own send
        // puts you behind your own event until the page comes back — so the status is armed
        // rather than mirrored: a timer starts when catch-up begins and only if it is STILL
        // running when the timer fires does the UI say so. Without this the header flickered
        // "up to date" → "catching up" → "up to date" on every message sent, which reads as a
        // fault. Disarmed the moment catch-up ends, and the reducer refuses a late `true`
        // anyway (`CatchUpSlowMsg`), so a fire that races a landing page changes nothing.
        let mutable catchUpTimer = 0.0
        let syncCatchUpTimer (model: ClientModel) =
            let consumer = model.EventConsumer
            if consumer.IsCatchingUp && not consumer.CatchUpIsSlow then
                // Idempotent: an armed timer is left to run, or a stream of pages would keep
                // pushing the deadline out and it would never fire.
                if catchUpTimer = 0.0 then
                    catchUpTimer <-
                        setTimeoutJs
                            (fun () ->
                                catchUpTimer <- 0.0
                                dispatchRef (CatchUpSlowMsg true))
                            catchUpQuietMs
            elif catchUpTimer <> 0.0 then
                clearTimeoutJs catchUpTimer
                catchUpTimer <- 0.0

        // Overlay each body's remote cursors, PACED BY THE FRAME: a render marks the push wanted
        // and the next animation frame performs it, at most once per frame however many renders
        // asked. Only editors whose cursor set changed are dispatched (idle empty→empty is
        // skipped), so a settled editor with no cursors is never disturbed; an editor that HAS
        // cursors is re-pushed regardless, because decorations are built from absolute positions
        // and the content moving underneath them invalidates those.
        //
        // This was a 150ms trailing debounce, waiting for the doc to go QUIET — a condition a
        // typing collaborator never meets, so their caret froze for as long as they typed and
        // jumped when they stopped. The comment above it said the wait kept decoration
        // transactions out of y-prosemirror's active-convergence window, where they "starve" its
        // rendering of remote content, and pointed at a two-peer E2E where a co-editor's mirror
        // had stayed blank.
        //
        // That is not what was happening, and it is worth writing down because the evidence
        // looked exactly like it. A ProseMirror widget decoration's DOM lives INSIDE the node it
        // is anchored in, so a co-editor's caret label parked in a heading makes that heading's
        // `textContent` read "Heading oneada". The E2E compared `textContent` to the words
        // exactly — so it was asserting the content AND that nobody's caret was in it. Pushing
        // carets sooner put one there sooner, and the wait then never settled: not late, never,
        // which reads precisely like lost content.
        //
        // The cheap tier now pins the real invariant (`EditorHarness`, two editors on two docs
        // relayed in-page): with a caret pushed on every frame throughout, both docs converge,
        // both editors render, and y-prosemirror's own write-back emits ZERO Yjs updates. The
        // decoration push is not a write, and it never was.
        let mutable pushQueued = false
        let pushPresences () =
            if not pushQueued then
                pushQueued <- true
                raf (fun () ->
                    pushQueued <- false
                    for kv in mountedBodies do
                        let key = kv.Key
                        let _, _, handle = kv.Value
                        let cursors = cursorsFor key
                        let prev = match lastPushed.TryGetValue key with | true, v -> v | _ -> []
                        if not (List.isEmpty cursors) || prev <> cursors then
                            lastPushed.[key] <- cursors
                            handle.PushPresences cursors)

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
                    let! status = fetchClaudeStatus () |> Async.AwaitPromise
                    if status.ok then
                        dispatchRef (
                            ClaudeStatusMsg
                                { SessionCredential = viewOf status.sessionKind status.sessionSignIn
                                  MineCredential = viewOf status.mineKind status.mineSignIn
                                  Owner = status.owner
                                  AgentAvailable = Some status.agent })
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
                        let! reply = postClaude route (claudeBody scope code token) |> Async.AwaitPromise
                        if not reply.ok then return Error reply.body
                        elif expectUrl then
                            match parseAuthorizeUrl reply.body with
                            | "" -> return Error "no authorize url in the reply"
                            | url -> return Ok (Some url)
                        else return Ok None
                    })
                scope

        // The model catalogue. Asked for once per open of the settings face, and answered
        // from the session's own kept copy after the first time — so this is a local round
        // trip, not a provider one, however often somebody opens the drawer.
        //
        // Re-asked on every open rather than fetched once at start, because the first ask
        // may well have failed for want of a connected account, and the settings face is
        // exactly where that gets fixed.
        let refreshModels () =
            Async.StartImmediate (
                async {
                    let! reply = fetchModelsAt (SessionRoute.relative SessionRoute.Models) |> Async.AwaitPromise
                    if not reply.ok then dispatchRef (ModelCatalogueMsg (ModelsUnavailable reply.body))
                    else
                        match Codec.fromString Codec.modelCatalogue reply.body with
                        | Ok models -> dispatchRef (ModelCatalogueMsg (ModelsLoaded models))
                        | Error reason -> dispatchRef (ModelCatalogueMsg (ModelsUnavailable reason))
                })

        // The GitHub panel's round-trips (Plan 14). Device flow: begin puts the user
        // code on screen, then this tab drives the session's poll at GitHub's stated
        // interval until the grant lands (a status probe then flips the flow to idle),
        // the human cancels, or the flow dies.
        let refreshGitHub () =
            Async.StartImmediate (
                async {
                    let! status = fetchGitHubStatus () |> Async.AwaitPromise
                    if status.ok then
                        dispatchRef (
                            GitHubStatusMsg
                                { SessionCredential = viewOf status.sessionKind status.sessionSignIn
                                  MineCredential = viewOf status.mineKind status.mineSignIn })
                })
        let rec pollGitHubWhileAwaiting () =
            Async.StartImmediate (
                async {
                    let interval =
                        match latestModel.GitHub.Flow with
                        | GitHubAwaitingApproval (_, _, _, interval) -> max 1 interval
                        | _ -> 0
                    do! Async.Sleep (interval * 1000)
                    match latestModel.GitHub.Flow with
                    | GitHubAwaitingApproval (userCode, verificationUri, scope, interval) ->
                        let! reply =
                            postClaude
                                (SessionRoute.relative (GitHub GitHubAction.Poll))
                                (githubBody scope "")
                            |> Async.AwaitPromise
                        if not reply.ok then dispatchRef (GitHubFlowMsg (GitHubError reply.body))
                        else
                            let outcome = parseDevicePoll reply.body
                            match outcome.status with
                            | "connected" -> refreshGitHub ()
                            | _ ->
                                if outcome.interval > interval then
                                    dispatchRef (GitHubFlowMsg (GitHubAwaitingApproval (userCode, verificationUri, scope, outcome.interval)))
                                pollGitHubWhileAwaiting ()
                    | _ -> ()
                })
        // The read surface (Plan 15): subscribe once, fold every frame. A malformed frame
        // is dropped rather than thrown — this is a best-effort push leg, and a stream
        // that dies on one bad line takes the whole surface down with it.
        openQueryStream
            (SessionRoute.relative SessionRoute.Queries)
            (fun data ->
                match Codec.fromString Codec.queryFrame data with
                | Ok frame -> dispatchRef (QueryFrameMsg frame)
                | Error _ -> ())
        |> ignore

        let githubAction (run: unit -> Async<Result<GitHubFlowState option, string>>) =
            dispatchRef (GitHubFlowMsg GitHubBusy)
            Async.StartImmediate (
                async {
                    match! run () with
                    | Error reason -> dispatchRef (GitHubFlowMsg (GitHubError reason))
                    | Ok (Some flow) ->
                        dispatchRef (GitHubFlowMsg flow)
                        pollGitHubWhileAwaiting ()
                    | Ok None ->
                        dispatchRef (GitHubFlowMsg GitHubIdle)
                        refreshGitHub ()
                })

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
                    // The connection panels are PROBED on open (they have no push leg);
                    // the query surface needs no re-probe, because its stream has been
                    // pushing since start.
                    toggleSettings ()
                    refreshClaude ()
                    refreshGitHub ()
                    refreshModels ()
              RevealSettings =
                fun () ->
                    revealSettings ()
                    refreshClaude ()
                    refreshGitHub ()
                    refreshModels ()
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
              GitHubConnect =
                fun () ->
                    let scope = match panelInput "[data-github-scope]" with "" -> "mine" | s -> s
                    githubAction (fun () ->
                        async {
                            let! reply =
                                postClaude
                                    (SessionRoute.relative (GitHub GitHubAction.Begin))
                                    (githubBody scope "")
                                |> Async.AwaitPromise
                            if not reply.ok then return Error reply.body
                            else
                                let began = parseDeviceBegin reply.body
                                match began.userCode with
                                | "" -> return Error "no device code in the reply"
                                | _ -> return Ok (Some (GitHubAwaitingApproval (began.userCode, began.verificationUri, scope, began.interval)))
                        })
              GitHubPasteToken =
                fun () ->
                    match panelInput "[data-github-token]" with
                    | "" -> dispatchRef (GitHubFlowMsg (GitHubError "paste a token first"))
                    | token ->
                        let scope = match panelInput "[data-github-scope]" with "" -> "mine" | s -> s
                        githubAction (fun () ->
                            async {
                                let! reply =
                                    postClaude
                                        (SessionRoute.relative (GitHub GitHubAction.Token))
                                        (githubBody scope token)
                                    |> Async.AwaitPromise
                                if not reply.ok then return Error reply.body else return Ok None
                            })
              GitHubDisconnect =
                fun scope ->
                    githubAction (fun () ->
                        async {
                            let! reply =
                                postClaude
                                    (SessionRoute.relative (GitHub GitHubAction.Disconnect))
                                    (githubBody scope "")
                                |> Async.AwaitPromise
                            if not reply.ok then return Error reply.body else return Ok None
                        })
              OpenTerminal = fun title -> connectionRef |> Option.iter (fun c -> c.OpenTerminal title)
              CloseTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.CloseTerminal id)
              TakeTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.TakeTerminal id)
              ReleaseTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.ReleaseTerminal id)
              RearmTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.RearmTerminal id)
              ReattachTerminal = fun id -> connectionRef |> Option.iter (fun c -> c.ReattachTerminal id)
              TypeIntoTerminal =
                fun id data -> connectionRef |> Option.iter (fun c -> c.TypeIntoTerminal id data)
              ResizeTerminal =
                fun id cols rows -> connectionRef |> Option.iter (fun c -> c.ResizeTerminal id cols rows)
              SendTerminalDraft =
                fun terminal author -> connectionRef |> Option.iter (fun c -> c.SendTerminalDraft terminal author)
              RetryNow =
                // Cut short whatever wait the lifecycle is in. On a refused peer that wait is
                // indefinite by design, so this is its only way back short of a reload.
                fun () -> pokeRetry ()
              ReopenSession =
                fun () ->
                    // A full navigation to the Manager, not a fetch: it launches the session
                    // if it is stopped and hands us on to wherever this deployment says the
                    // session lives.
                    //
                    // What the reload costs depends on the address. Under a `{id}` template
                    // we land on the SAME origin, so the doc in IndexedDB is still this
                    // session's and syncs straight back. Addressed by port we land somewhere
                    // new, and anything written since it stopped stays behind — which is why
                    // the card says so before this runs.
                    //
                    // The anchor's href is the same URL, so this is an enhancement rather
                    // than the mechanism: with no JS the link still works.
                    match latestModel.Manager, latestModel.Session with
                    | Some origin, Some sessionId ->
                        navigateTo (sprintf "%s/sessions/%s/open" origin (SessionId.value sessionId))
                    | _ -> ()
              FocusPane = PaneShell.toPane
              FocusChat = PaneShell.toChatItem
              FocusDvr = fun id -> PaneShell.toDvrControl (TerminalId.value id) }

        let el = appRoot ()
        // Take over the server-rendered shell (see `clearChildren`): from here Lit owns it.
        clearChildren el

        // Render the Lit view on every model change. Lit diffs into `#app`, so the focused
        // textarea and its caret survive; only the timeline scroll is restored by hand.
        let setState (model: ClientModel) (dispatch: Ylmish.Program.Message<ClientMsg> -> unit) =
            dispatchRef <- fun msg -> dispatch (Ylmish.Program.Message.User msg)
            latestModel <- model
            let scroll = surfaceScroll PinnedSurfaces
            Lit.render (unbox el) (View.view actions model dispatchRef)
            restoreSurfaceScroll PinnedSurfaces scroll
            // Mount/dispose the rich editors on their body hosts (bound to live fragments), then
            // overlay collaborators' cursors: remote carets in each body editor, and title carets
            // measured against the just-rendered input.
            syncRichBodies ()
            syncTerminalInputs ()
            syncKeyframes model
            replays.Sync model
            // The live screens, and the size the holder's viewport actually is. Both AFTER
            // the render: one folds records into an emulator whose serialization the next
            // render draws, the other measures a box that has to exist first.
            screens.Sync model
            syncScreenSize model
            // The terminals column's open state is a class on the shell root, like the
            // sidebar's — presentation, so a re-render never fights it — but driven FROM the
            // model, because unlike the sidebar this column's visibility is something the app
            // itself changes (selecting a terminal opens it).
            PaneShell.setOpen model.TerminalsOpen
            // Keep a slot rule running for every open terminal: a person may be mid-command
            // in more than one, and each slot follows its own command line.
            syncTerminalSlots model
            syncCatchUpTimer model
            pushPresences ()
            placeTitleCursorsAll ()
            // The tab's name, which lives outside `#app` and so is the model's to push rather
            // than Lit's to render. The NAME is computed in the model (`tabTitle`); this only
            // applies it, and only on a change — assigning `document.title` every render is a
            // write the browser need not be asked to make.
            let tab = ClientModel.tabTitle model
            if Browser.Dom.document.title <> tab then Browser.Dom.document.title <- tab

        App.makeProgram doc initial
        |> Program.withSetState setState
        |> Program.run

        // Renders keep the reader's place (`setState`); this keeps it across the other thing
        // that moves it, a viewport that changed size under a laid-out surface.
        keepSurfacesPinned PinnedSurfaces
        // And the split between the two columns is the reader's to set, not the theme's.
        PaneShell.installPaneResize ()

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

        // Durable history, out of this client's own store, BEFORE anything asks the network
        // and regardless of what `/me` is about to say (Plan 20). The probe decides whether
        // this client may CONNECT; it has never had any business deciding whether a client may
        // read what it was already given. That it did is why an offline open rendered an empty
        // conversation rather than the one it had been reading.
        // The worker first, because it is what makes the NEXT cold open work; this one is
        // already served. Fire and forget: nothing here depends on it, and a client that
        // cannot have one loses only the offline open.
        registerWorker (SessionRoute.relative ServiceWorker)

        let! historyCache = openHistoryCache ()
        let! transcriptCaches = openTranscriptCaches ()
        do! App.EventFetch.replay historyCache (fun msg -> dispatchRef msg)
        // After the events, never beside them: a terminal exists because an event said so, and
        // records folded before that event has been folded have nowhere to land.
        do! App.TranscriptFetch.replay transcriptCaches (fun msg -> dispatchRef msg)

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
                // `storing` sits UNDER the policy, so only a settled answer is kept — a
                // retried fetch stores once, and a failed one stores nothing.
                App.EventFetch.overHttp (App.EventFetch.storing historyCache httpGet) SessionRoute.relative None
                |> Resilience.Policy.guard
                    (App.EventFetch.policy Resilience.Policy.sleep jsRandom (fun attempt ->
                        App.EventFetch.retrying attempt
                        |> Option.iter (fun health -> dispatchRef (EventFeedMsg health))))
            // Terminal history rides the same HTTP leg, by the same cursor, for the same
            // payoff: a reload replays a terminal out of this client's own store and only what
            // happened since crosses the network. No resilience policy on it — unlike the event
            // feed, a failed read here is re-armed by the next record or availability hint
            // that arrives, so there is nothing for a retry schedule to add.
            let transcripts =
                App.TranscriptFetch.overHttp transcriptCaches httpGet SessionRoute.relative None
            let options =
                { App.ConnectOptions.defaults with
                    FetchEvents = Some feed
                    FetchTranscripts = Some transcripts
                    // A terminal's screen seeds this client's emulator. The transcript stays
                    // the record; this is the view, and a peer that arrives mid-session gets
                    // one frame instead of every byte the terminal ever printed.
                    OnTerminalSnapshot = fun id seq screen -> screens.Snapshot id seq screen
                    // The model is the read position (see `ConnectOptions.ReadPosition`):
                    // `latestModel` is kept current by `setState`, so a fold rolled back by
                    // a racing doc update is visibly behind and gets re-read.
                    ReadPosition = Some (fun () -> latestModel.EventConsumer.LastProcessedOffset)
                    // Same rule one feed over: a client that just replayed a terminal out of
                    // its own store must resume where that got to, not at line 0.
                    TranscriptReadPosition =
                        Some (fun terminal ->
                            latestModel.TerminalFeeds
                            |> Map.tryFind terminal
                            |> Option.map (fun feed -> feed.ReadThrough)
                            |> Option.defaultValue 0) }
            let openChannel =
                Resilience.Policy.guard
                    (App.SessionChannel.policy Resilience.Policy.sleep jsRandom)
                    (fun () -> connectChannel (absolute (SessionRoute.relative Signal)))

            // The session leg. The RULES — announce, open, serve, and come back only for a
            // session that was accepted — are `App.SessionLifecycle`; this supplies the
            // browser's four ports and nothing else.
            do!
                App.SessionLifecycle.run
                    (App.SessionLifecycle.supervision jsRandom)
                    { Open = openChannel
                      Serve =
                        fun resumeAfter dispatch carrier ->
                            async {
                                // Supervised at the transport boundary, exactly as the event
                                // feed's resilience policy is composed here and nowhere else:
                                // `App.connect` receives a channel that already knows how to
                                // notice its own death, and holds no notion of heartbeats.
                                let channel = Link.supervise Link.LinkPolicy.shipped carrier
                                let connection =
                                    App.connect
                                        { options with ResumeAfter = resumeAfter }
                                        doc
                                        registry
                                        texts
                                        hello
                                        dispatch
                                        channel
                                connectionRef <- Some connection
                                do! connection.Run
                                connectionRef <- None
                            }
                      ReadPosition = fun () -> latestModel.EventConsumer.LastProcessedOffset
                      // Always `true`: a page that is still open is a client that still wants
                      // its session. The lifecycle ends when the page does.
                      WaitBeforeRetry = waitBeforeRetry
                      Dispatch = fun msg -> dispatchRef msg }
    }

Async.StartImmediate (start ())
