module Yession.Browser.EditorHarness

// A host-free browser harness for the rich-editor E2E. Mounts a ProseMirror editor on a fresh
// Yjs fragment into `#host` and exposes its serialized Markdown as `window.__md`. There is NO
// Session Process, NO WebRTC and NO native addon here — just the editor and a doc — so the
// editor-rendering E2E (`Tag.needs [Browser]`) runs wherever Chromium exists, decoupled from
// `node-datachannel`. Pure F# (the no-authored-JS invariant holds); Fable-compiles alongside
// the app browser entry, and the `Browser`-cap test build esbuilds it into the served bundle.
//
// It also exercises presence cursors headlessly-but-in-a-browser: the editor reports its own
// selection (base64 relative anchor/head) through `reportFocus`; `window.__pushRemote(name)`
// feeds that same selection back in as a *remote* peer's cursor, so the on-screen decorations
// (the caret widget + label and the selection highlight) render for the E2E to assert on.
//
// Module-level `do` runs on import — module scripts are deferred, so `#host` already exists.

open Fable.Core
open Lit
open Yjs
open Yession.Domain
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Chat
open Yession.App

[<Emit("document.getElementById('host')")>]
let private host : obj = jsNative

/// The replay mount (Plan 13, stage 3e). It shares this page rather than getting one of its
/// own because it is the same KIND of thing — a host-free surface that needs a real browser
/// and nothing else — and a second harness would be a second bundle, a second page and a
/// second static server for one `create` call.
[<Emit("document.getElementById('replay')")>]
let private replayHost : Browser.Types.Element = jsNative

[<Emit("document.getElementById('replay-gappy')")>]
let private gappyReplayHost : Browser.Types.Element = jsNative

[<Emit("(function(f){ window.__md = f; })($0)")>]
let private exposeMd (f: unit -> string) : unit = jsNative

[<Emit("(function(f){ window.__pushRemote = f; })($0)")>]
let private exposePush (f: string -> unit) : unit = jsNative

/// How many times Enter has asked to send. The harness mounts the editor exactly as the
/// COMPOSER does (`onSubmit` supplied), so the E2E drives the real binding: Enter sends and
/// inserts nothing, Alt+Enter is the new line. A counter rather than a callback because what
/// the test needs to know is "did it fire", and the send itself belongs to the app.
[<Emit("(function(n){ window.__sends = n; })($0)")>]
let private exposeSends (n: int) : unit = jsNative

let private doc = Y.Doc.Create ()
let private fragment = doc.getXmlFragment "body"

do
    // The editor reports its local selection here; keep the latest so the harness can replay it
    // as a remote peer's cursor on demand.
    let mutable lastSelection : (string * string) option = None
    let mutable sends = 0
    exposeSends 0
    let handle =
        Editor.mountEditor
            host
            fragment
            false
            (fun sel -> lastSelection <- sel)
            (Some (fun () ->
                sends <- sends + 1
                exposeSends sends))
            // The harness IS the composer, so it wears the composer's prompt: the browser
            // tier can then read the placeholder where the editor really draws it.
            Dom.Text.composerPlaceholder
    exposeMd (fun () -> Markdown.ofFragment fragment)
    exposePush (fun name ->
        match lastSelection with
        | Some (anchor, head) ->
            handle.PushPresences
                [ ({ Colour = "hsl(200, 70%, 55%)"
                     Selection = "hsla(200, 70%, 55%, 0.25)"
                     Name = name
                     Anchor = anchor
                     Head = head } : Editor.RemoteBodyCursor) ]
        | None -> ())

    // The replay, mounted from a `.cast` rebuilt by the very function the client uses. This
    // is the one part of stage 3e no DOM-free test can reach: whether `asciinema-player`'s
    // named export actually resolves through the bundle and renders the recording.
    let cast =
        TranscriptReplay.castWithMarkers
            { Width = 80; Height = 24; Timestamp = 0L }
            [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\r\n" }
              1, { At = 0.1; Kind = TranscriptOutput; Data = "total 0\r\n" } ]
            [ 0.0, "ls -la" ]
    // The chapter rides IN the cast (Plan 25, stage 1) — `castWithMarkers` above — because
    // that is how the client builds one, and whether chapters survive the bundle is the same
    // question the import itself is. A `startAt` is deliberately absent here: it would skip
    // past the very frame the replay assertion waits for.
    Replay.mount
        replayHost
        { Cast = cast
          StartAt = None
          Poster = None
          BehindLive = None }
        None
    |> ignore

    // The same machinery over a recording with DEAD AIR in it, which is the shape the pane
    // actually replays and the one the mount above cannot fail in: thirty seconds of nothing
    // between the first command and the second, a chapter on the far side of the gap, and a
    // start position naming it.
    //
    // Under the player's `markers` option all three disagree — the option list stays on the
    // recording's raw clock while the events are idle-compressed as they load, so the chapter
    // past the gap becomes the last event (and is dropped from the chapter list by its
    // `time < duration` filter), the duration reads uncompressed, and `startAt` lands short.
    // With the chapters written into the cast they are all one clock, and a reader reaches
    // the second command in about as long as it takes to press play.
    let gappy =
        TranscriptReplay.castWithMarkers
            { Width = 80; Height = 24; Timestamp = 0L }
            [ 0, { At = 0.0; Kind = TranscriptInput; Data = "echo first\r\n" }
              1, { At = 0.1; Kind = TranscriptOutput; Data = "first\r\n" }
              2, { At = 30.0; Kind = TranscriptInput; Data = "echo second\r\n" }
              3, { At = 30.1; Kind = TranscriptOutput; Data = "second\r\n" } ]
            [ 0.0, "echo first"
              30.0, "echo second" ]
    Replay.mount
        gappyReplayHost
        { Cast = gappy
          StartAt = Some 30.0
          Poster = None
          BehindLive = None }
        None
    |> ignore

// --- Two peers converging on one body (the caret write-back) -----------------------------
//
// What only a browser can answer about remote carets, and what no cheap test could reach
// before this: whether pushing presence DECORATIONS while content is still arriving stops the
// content arriving at all.
//
// The reason it needs saying at all. `PushPresences` dispatches a ProseMirror transaction
// carrying no steps — but ProseMirror runs every plugin's `view.update` on every state update,
// and `ySyncPlugin`'s hook is not gated on `docChanged`: it calls `_prosemirrorChanged`, which
// walks the WHOLE document and reconciles it back into Yjs. The binding exempts its own
// dispatches by holding a mutex across them; an unsolicited one holds nothing. So a caret push
// is a write, and a caret push during convergence is a write that races the content.
//
// This surface is that race, reduced: two docs, two editors, relayed to each other IN THIS
// PAGE. No WebRTC, no Session Process, no native addon — so it runs in the `Browser` tier
// wherever Chromium exists, in seconds. The two-peer WebRTC E2E can also see this bug, but
// only under enough load to lose the race, which cost two runs of the gate to learn once.

[<Emit("document.getElementById('peer-a')")>]
let private peerAHost : obj = jsNative

[<Emit("document.getElementById('peer-b')")>]
let private peerBHost : obj = jsNative

/// Start or stop pushing presence decorations into the MIRROR on every animation frame.
[<Emit("(function(f){ window.__caretStorm = f; })($0)")>]
let private exposeStorm (f: bool -> unit) : unit = jsNative

/// How many frames the storm has actually pushed on. Anti-vacuity: a convergence assertion
/// passes trivially if the storm never ran, and "never ran" and "ran and was harmless" look
/// identical from the outside.
[<Emit("(function(n){ window.__caretPushes = n; })($0)")>]
let private exposeCaretPushes (n: int) : unit = jsNative

[<Emit("requestAnimationFrame(() => $0())")>]
let private onFrame (f: unit -> unit) : unit = jsNative

/// Count the Yjs updates a doc takes from `ySyncPlugin`'s OWN write-back — the ones whose
/// origin is `ySyncPluginKey`, which is what `_prosemirrorChanged` tags its transaction with.
///
/// This is the instrument the caret question turns on, and it exists because the write-back
/// is otherwise completely silent: it walks the whole document, it can emit CRDT operations,
/// and it leaves no trace anywhere that a test could read. Counting from OUR side rather than
/// patching the library keeps it honest — the number is real doc updates, not a hook we hoped
/// was called.
/// Counts BOTH, and the second one is what makes the first believable: `__docUpdates` is every
/// update this doc took, `__writebacks` only those the write-back produced. A write-back count
/// of zero means "drawing a caret wrote nothing" only if the doc was moving at all — otherwise
/// it means the observer was never wired up, and the two look identical from a test.
[<Emit("""(function (doc, key) {
  let all = 0, back = 0
  window.__docUpdates = 0
  window.__writebacks = 0
  doc.on('update', function (_update, origin) {
    all++; window.__docUpdates = all
    if (origin === key) { back++; window.__writebacks = back }
  })
})($0, $1)""")>]
let private countWritebacks (doc: Y.Doc) (syncKey: obj) : unit = jsNative

[<Import("ySyncPluginKey", "y-prosemirror")>]
let private ySyncPluginKey : obj = jsNative

[<Emit("(function(f){ window.__convState = f; })($0)")>]
let private exposeConvState (f: unit -> string) : unit = jsNative

/// The two docs' content and the two editors' rendered text, side by side. `docA`/`docB` are
/// what the CRDT holds; `pmA`/`pmB` are what each editor actually put on screen. A gap between
/// a doc and its own editor is a binding that stopped rendering; a gap between the two docs is
/// a relay that stopped carrying.
[<Emit("""JSON.stringify({
  docA: $0, docB: $1, caretPushes: $2,
  writebacks: window.__writebacks,
  pmA: document.querySelector('#peer-a .ProseMirror')?.textContent ?? null,
  pmB: document.querySelector('#peer-b .ProseMirror')?.textContent ?? null
})""")>]
let private convStateJson (docA: string) (docB: string) (pushes: int) : string = jsNative

// --- The performance surface's interop ---------------------------------------------------

[<Emit("performance.now()")>]
let private now () : float = jsNative

/// Resolve on the next animation frame — the browser saying "I have painted". Every latency
/// here is measured against this and nothing else.
[<Emit("new Promise(r => requestAnimationFrame(() => r()))")>]
let private nextFrame () : JS.Promise<unit> = jsNative

/// Every keystroke reaching `host`, timed from the browser's OWN event timestamp to the frame
/// it paints on. `event.timeStamp` shares `performance.now()`'s time origin, so the difference
/// is real input-to-paint including the browser's dispatch — which is what a person means by
/// "does typing feel instant".
///
/// Deliberately NOT the Event Timing API: `PerformanceEventTiming.duration` is rounded to 8ms
/// for privacy. That is a fine threshold for judging one interaction and useless for watching
/// a trend move, which is this suite's whole job.
///
/// Capturing (`true`), so a keystroke is timed from before the editor sees it.
[<Emit("""(function (host, take) {
  host.addEventListener('keydown', function (e) {
    requestAnimationFrame(function () { take(performance.now() - e.timeStamp) })
  }, true)
})($0, $1)""")>]
let private onKeystrokePainted (host: obj) (take: float -> unit) : unit = jsNative

/// Two named number series as one JSON object — the shape every scenario returns.
[<Emit("JSON.stringify({ [$0]: $1, [$2]: $3 })")>]
let private twoSeries (a: string) (xs: float[]) (b: string) (ys: float[]) : string = jsNative

[<Emit("(function(f){ window.__benchSeed = f; })($0)")>]
let private exposeSeed (f: int -> JS.Promise<int>) : unit = jsNative

[<Emit("(function(f){ window.__benchCarets = f; })($0)")>]
let private exposeCarets (f: int -> JS.Promise<string>) : unit = jsNative

[<Emit("(function(f){ window.__benchReset = f; })($0)")>]
let private exposeBenchReset (f: unit -> unit) : unit = jsNative

[<Emit("(function(f){ window.__benchTyping = f; })($0)")>]
let private exposeTyping (f: unit -> string) : unit = jsNative

/// Markdown of roughly `chars` characters, as paragraphs rather than one enormous line: what
/// the reconciliation walks is NODES, so a document's structure is part of what is being
/// measured and a single block would flatter it.
let private filler (chars: int) : string =
    let sentence = "The quick brown fox jumps over the lazy dog. "
    let perPara = 200
    let one = (String.replicate (perPara / sentence.Length + 1) sentence).Substring (0, perPara)
    String.concat "\n\n" (List.replicate (max 1 (chars / perPara)) one)

/// Set by the bench wiring below, called by the relay. A mutable seam rather than a second
/// relay registration, because two listeners on one doc would make the ORDER of "carry it" and
/// "start the clock" something the reader has to guess at.
let mutable private relayObserved : unit -> unit = ignore

let private docA = Y.Doc.Create ()
let private docB = Y.Doc.Create ()

do
    // The relay: each doc's local updates become the other's remote ones, exactly as the
    // Session Process relays them between two browsers — minus the transport.
    DocSync.onLocalUpdate docA (fun payload -> DocSync.applyRemote docB payload) |> ignore
    DocSync.onLocalUpdate docB (fun payload ->
        relayObserved ()
        DocSync.applyRemote docA payload)
    |> ignore

    // Watch the MIRROR: it is the one whose carets are pushed, so it is the one whose
    // write-back would race the content arriving into it.
    countWritebacks docB ySyncPluginKey

    let fragmentA = docA.getXmlFragment "shared"
    let fragmentB = docB.getXmlFragment "shared"

    // BOTH editable, because that is the arrangement the bug appears in: a co-editor JOINS the
    // draft rather than watching it, so the second editor is a composer bound to the same body,
    // with its own binding writing back into its own doc. A read-only mount is the easier case
    // and proves less — it was the first thing this surface tried, and it converged happily.
    let mutable selectionA : (string * string) option = None
    let mutable selectionB : (string * string) option = None
    Editor.mountEditor peerAHost fragmentA false (fun sel -> selectionA <- sel) None "" |> ignore
    let mirror = Editor.mountEditor peerBHost fragmentB false (fun sel -> selectionB <- sel) None ""

    let mutable storming = false
    let mutable pushes = 0
    exposeCaretPushes 0
    // A's caret, drawn in B — decoded against B's own doc, so the positions are real rather
    // than replayed constants. Before A has reported a selection there is nothing to draw and
    // the frame still counts as a push: what is under test is the dispatch, not the geometry.
    let rec storm () =
        if storming then
            (match selectionA with
             | Some (anchor, head) ->
                 mirror.PushPresences
                     [ ({ Colour = "hsl(200, 70%, 55%)"
                          Selection = "hsla(200, 70%, 55%, 0.25)"
                          Name = "ada"
                          Anchor = anchor
                          Head = head } : Editor.RemoteBodyCursor) ]
             | None -> mirror.PushPresences [])
            pushes <- pushes + 1
            exposeCaretPushes pushes
            onFrame storm
    // What each side holds, as one JSON blob. A convergence failure has four candidate
    // stories — the author never wrote it, the relay never carried it, the co-editor's doc has
    // it but its editor never rendered it, or something wrote over it — and they are
    // indistinguishable from the DOM alone. This tells them apart in the failure message
    // instead of in a debugging session.
    exposeConvState (fun () ->
        convStateJson
            (Markdown.ofFragment fragmentA)
            (Markdown.ofFragment fragmentB)
            pushes)

    exposeStorm (fun on ->
        if on && not storming then
            storming <- true
            onFrame storm
        else storming <- on)

    // --- What a person waits for ----------------------------------------------------------
    //
    // Three latencies and one budget, over the same two peers. `check` proves behaviour and
    // says nothing about time, so a cost like y-prosemirror's whole-document reconciliation on
    // every caret push (`view.update` is not gated on `docChanged`) degrades silently until
    // somebody notices typing feels bad on a long message.
    //
    // Every measurement is the same primitive: mark a start, and let the browser say when it
    // painted. Three uses, one thing to get right.
    //
    // Sampled at several document SIZES by the driver, which is the whole point — the concern
    // is O(document), and a number taken at one size cannot show a complexity regression at
    // all. The size sweep is what turns four latencies into a slope.

    let typeSamples = ResizeArray<float> ()
    let receiveSamples = ResizeArray<float> ()

    // Local echo: the author's own keystroke, from the browser's event timestamp to the frame
    // it paints on. Peer B is the one the driver types into, so B is the one listened to.
    onKeystrokePainted peerBHost typeSamples.Add

    // The other half of the same keystroke: the co-editor's screen. Marked at the relay, which
    // is where an update "arrives" — everything after it (apply, render, paint) is what the
    // person on the other end is waiting through.
    relayObserved <- fun () ->
        let t0 = now ()
        onFrame (fun () -> receiveSamples.Add (now () - t0))

    exposeBenchReset (fun () ->
        typeSamples.Clear ()
        receiveSamples.Clear ())

    exposeTyping (fun () ->
        twoSeries "type" (typeSamples.ToArray ()) "receive" (receiveSamples.ToArray ()))

    // Fill BOTH docs to roughly `chars`, through the real relay, and settle. One write rather
    // than a keystroke drip: this is setup, and nothing here is timed.
    exposeSeed (fun chars ->
        async {
            Markdown.intoFragment (filler chars) fragmentA
            do! nextFrame () |> Async.AwaitPromise
            do! nextFrame () |> Async.AwaitPromise
            return (Markdown.ofFragment fragmentB).Length
        }
        |> Async.StartAsPromise)

    // The caret push, timed twice: the WORK (everything `view.dispatch` sets off, which is the
    // reconciliation this suite exists to watch) and the WAIT (until it is on screen).
    //
    // A frame is yielded between samples so the browser can actually paint. A tight loop would
    // measure a hot cache and a starved compositor, which is nobody's experience.
    exposeCarets (fun samples ->
        async {
            let push = ResizeArray<float> ()
            let paint = ResizeArray<float> ()
            for _ in 1 .. samples do
                // The decoration set is rebuilt on every `setMeta` regardless of whether the
                // position moved (`presenceDecorationsPlugin`'s `apply`), so one position is
                // enough to make the work real. It is B's OWN reported selection, which means
                // it is a position that resolves in B's doc rather than a replayed constant.
                let cursors =
                    match selectionB with
                    | Some (anchor, head) ->
                        [ ({ Colour = "hsl(200, 70%, 55%)"
                             Selection = "hsla(200, 70%, 55%, 0.25)"
                             Name = "ada"
                             Anchor = anchor
                             Head = head } : Editor.RemoteBodyCursor) ]
                    | None -> []
                let t0 = now ()
                mirror.PushPresences cursors
                push.Add (now () - t0)
                do! nextFrame () |> Async.AwaitPromise
                paint.Add (now () - t0)
            return twoSeries "push" (push.ToArray ()) "paint" (paint.ToArray ())
        }
        |> Async.StartAsPromise)

// --- The shell, host-free (Plan 14, stage 2) --------------------------------------------
//
// The same page, for the same reason the replay shares it: this is the same KIND of thing —
// a surface that needs a real browser and nothing else. What only a browser can answer here
// is where FOCUS goes when a chip in the chat opens a tab in the pane, and whether the tab
// strip is a tablist the arrow keys actually walk. Both are DOM-swap behaviours a rendered
// string cannot show, and neither needs a Session Process, a channel or a native addon.
//
// A minimal Elmish: `View.view` over a `ClientModel`, re-rendered on dispatch. The reducer,
// the view and the focus moves are the app's own — only the loop is local, because Program
// would want a doc and a connection this page deliberately does not have.

[<Emit("document.getElementById('shell')")>]
let private shellHost : obj = jsNative

/// The shell's own container class, taken from `Style.app` rather than written into the
/// harness page — the served document sets exactly this on `<main id="app">`, and a second
/// copy in HTML would be a layout free to drift from the one people get.
[<Emit("document.getElementById('shell').className = $0")>]
let private dressShell (className: string) : unit = jsNative

/// A session that has run one command: one open terminal, one finished block, and the two
/// transcript records it produced. Enough for a chip to render in the chat and for its tab
/// to have something to show.
let private shellModel : ClientModel =
    let expect = function Ok v -> v | Error e -> failwith e
    let terminalId : TerminalId = TerminalId.create "term-harness" |> expect
    /// A second terminal, in LIVE mode and held by this peer — the screen that takes
    /// keystrokes (Plan 14, stage 6). Its own terminal rather than the first one's, so the
    /// block-mode flows above keep a block-mode terminal to run in.
    let liveId : TerminalId = TerminalId.create "term-live" |> expect
    /// A CLOSED terminal, for the list's other half — a recording rather than a place to type.
    let doneId : TerminalId = TerminalId.create "term-done" |> expect
    let blockId : BlockId = BlockId.create "block-harness" |> expect
    /// A burst: three commands ONE agent turn ran, so the chat has a task card to draw
    /// (Plan 20, stage 4). Three rather than two, and in three different states, because
    /// what a card does that a chip cannot is count and order them — a burst that was all
    /// one state would exercise the disclosure and nothing else.
    let agentTurn : AgentTurnId = AgentTurnId.create "turn-harness" |> expect
    let burstOk : BlockId = BlockId.create "block-burst-ok" |> expect
    let burstFailed : BlockId = BlockId.create "block-burst-failed" |> expect
    let burstRunning : BlockId = BlockId.create "block-burst-running" |> expect
    let peerId : PeerId = PeerId.create "ada" |> expect
    let messageId : MessageId = MessageId.create "msg-harness" |> expect
    /// What the agent actually says, which is the hard case for a phone: a fenced block whose
    /// lines are far wider than the screen, and prose carrying tokens no line break fits
    /// inside — a path, a URL. Nothing here may be allowed to size the timeline, or the whole
    /// conversation slides sideways under a header that stays put (photographed on iOS).
    let wideId : MessageId = MessageId.create "msg-wide" |> expect
    let wideBody =
        String.concat
            "\n"
            [ "Cleared the broken checkout at /home/user/.yession/sessions/AAZFRYD11S65Q4P64KHATP8YYG/workspace/repos/NickDarvey/yession"
              "and retried, see https://github.com/NickDarvey/yession/actions/runs/1234567890123/job/9876543210987."
              ""
              "```"
              "git -C /home/user/.yession/sessions/AAZFRYD11S65Q4P64KHATP8YYG/workspace/repos clone --depth 1 --filter=blob:none https://github.com/NickDarvey/yession.git"
              "```" ]
    let offset (n: int64) : EventOffset = EventOffset.create n |> expect
    /// Enough said, under ONE author, that the column genuinely scrolls and the pinned author
    /// line has something riding under it. Both are preconditions for the thing the rail's
    /// jump has to get right — landing its target where a person can READ it, rather than
    /// beneath the line saying who spoke — and a two-message fixture can show neither, so a
    /// test written against one passes whatever the jump does.
    let filler : ConversationItem list =
        [ for i in 1 .. 16 ->
            { MessageId = MessageId.create (sprintf "msg-filler-%d" i) |> expect
              Author = PeerRef peerId
              Body = sprintf "and then line %d, which is here to make the column long" i
              Status = Complete
              Kind = ConversationItemKind.Message
              Offset = offset (int64 (10 + i))
              Woke = None } ]
    { ClientModel.init { PeerId = peerId; DisplayName = "swift-heron" } with
        Connection = Connected
        Session = Some (SessionId.create "harness" |> expect)
        // One marked message, so the landmark rail has a stroke to draw. Where a stroke
        // LANDS is arithmetic a model test settles; whether it lands beside the reading
        // column or on top of it is geometry, and only a rendered page knows that.
        Synced = { SyncedSessionState.empty with Landmarks = Map.ofList [ messageId, true ] }
        Conversation =
            { Items =
                [ { MessageId = messageId
                    Author = PeerRef peerId
                    Body = "ship it"
                    Status = Complete
                    Kind = ConversationItemKind.Message
                    Offset = offset 1L
                    Woke = None }
                  { MessageId = wideId
                    Author = ActorRef.Agent
                    Body = wideBody
                    Status = Complete
                    Kind = ConversationItemKind.Message
                    Offset = offset 3L
                    Woke = None } ]
                @ filler
              ActiveAgentMessages = Map.empty; WokenTurn = None }
        Timeline =
            { TimelineProjection.empty with
                TerminalItems =
                    [ TimelineBlock (offset 2L, terminalId, blockId)
                      TimelineBlock (offset 3L, terminalId, burstOk)
                      TimelineBlock (offset 4L, terminalId, burstFailed)
                      TimelineBlock (offset 5L, terminalId, burstRunning) ]
                BlockTurns =
                    Map.ofList
                        [ BlockId.value burstOk, agentTurn
                          BlockId.value burstFailed, agentTurn
                          BlockId.value burstRunning, agentTurn ] }
        Terminals =
            { Terminals =
                [ { TerminalId = terminalId
                    Title = TerminalTitle.fromProse "build"
                    OpenedBy = PeerRef peerId
                    Sandbox = Some SandboxRef.defaultRef
                    Renewable = false
                    IsOpen = true
                    ClosedReason = None
                    Lease = None
                    IntegrationLost = false
                    Blocks =
                      [ { BlockId = blockId
                          QueueId = None
                          Authority = Authority.ofAuthor (PeerRef peerId)
                          Command = "ls -la"
                          Background = false
                          FromSeq = 0
                          ToSeq = Some 2
                          Status = BlockFinished (CommandSucceeded 0) }
                        { BlockId = burstOk
                          QueueId = None
                          Authority = Authority.agentFor (PeerRef peerId)
                          Command = "npm run build"
                          Background = false
                          FromSeq = 2
                          ToSeq = Some 2
                          Status = BlockFinished (CommandSucceeded 0) }
                        { BlockId = burstFailed
                          QueueId = None
                          Authority = Authority.agentFor (PeerRef peerId)
                          Command = "npm test"
                          Background = false
                          FromSeq = 2
                          ToSeq = Some 2
                          Status = BlockFinished (CommandFailed 1) }
                        { BlockId = burstRunning
                          QueueId = None
                          Authority = Authority.agentFor (PeerRef peerId)
                          Command = "git status"
                          Background = true
                          FromSeq = 2
                          ToSeq = None
                          Status = BlockRunning }
                        // Enough history that the scrollback actually OVERFLOWS its box.
                        // "Show in terminal" (Plan 25, stage 3) scrolls to a command, and a
                        // history that fits on screen is one where every scroll is a no-op —
                        // a surface on which that case could not fail.
                        yield!
                          [ for i in 1 .. 24 ->
                              { BlockId = BlockId.create (sprintf "block-filler-%d" i) |> expect
                                QueueId = None
                                Authority = Authority.ofAuthor (PeerRef peerId)
                                Command = sprintf "echo line %d" i
                                Background = false
                                FromSeq = 2
                                ToSeq = Some 2
                                Status = BlockFinished (CommandSucceeded 0) } ] ]
                    DroppedBytes = 0 }
                  { TerminalId = liveId
                    Title = TerminalTitle.fromProse "shell"
                    OpenedBy = PeerRef peerId
                    Sandbox = Some SandboxRef.defaultRef
                    Renewable = false
                    IsOpen = true
                    ClosedReason = None
                    Lease = Some (PeerRef peerId)
                    IntegrationLost = false
                    Blocks = []
                    DroppedBytes = 0 }
                  // A finished one, so the terminal LIST (Plan 20, stage 0) has both its
                  // halves here: the working set, and the history it is a census of. Without
                  // it the list would be exercised over open terminals only, which is the
                  // half that was never the problem.
                  { TerminalId = doneId
                    Title = TerminalTitle.fromProse "install"
                    OpenedBy = PeerRef peerId
                    Sandbox = Some SandboxRef.defaultRef
                    Renewable = false
                    IsOpen = false
                    ClosedReason = Some "exit 0"
                    Lease = None
                    IntegrationLost = false
                    Blocks = []
                    DroppedBytes = 0 } ] }
        // The live terminal has a recording behind it too — that is what makes it
        // rewindable (Plan 14, stage 7), and a DVR with nothing recorded is a control with
        // nothing to do.
        TerminalFeeds =
            Map.ofList
                [ terminalId,
                  { Records =
                      Map.ofList
                          [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\n" }
                            1, { At = 0.1; Kind = TranscriptOutput; Data = "total 0\n" } ]
                    KnownLength = 2
                    ReadThrough = 2
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } }
                  liveId,
                  { Records =
                      Map.ofList
                          [ 0, { At = 0.0; Kind = TranscriptOutput; Data = "earlier output\r\n" }
                            1, { At = 0.2; Kind = TranscriptOutput; Data = "vim ~/notes\r\n" } ]
                    KnownLength = 2
                    ReadThrough = 2
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } }
                  doneId,
                  { Records = Map.ofList [ 0, { At = 0.0; Kind = TranscriptOutput; Data = "installed\r\n" } ]
                    KnownLength = 1
                    ReadThrough = 1
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } } ]
        // SHUT to begin with, like a fresh client: the phone case is about what happens when
        // a chip brings the pane on screen, which is nothing to watch if it is already there.
        TerminalScreens = Map.ofList [ liveId, "\u001b[32mvim ~/notes\u001b[0m" ]
        // The two terminals this peer opened, pinned as the events fold would have pinned
        // them (Plan 20, stage 1). Set by hand because this model is BUILT rather than folded
        // — and without them the strip would hold only whatever is being previewed, which is
        // a fresh client's state rather than a working one.
        Pins = [ TerminalTab terminalId; TerminalTab liveId ]
        TerminalsOpen = false }

/// Every byte the live screen decided to send, for the E2E to read back. The keystroke
/// translation is the whole of what a terminal front end does with a keyboard event, and it
/// is the one part of it that only a real browser can exercise: `KeyboardEvent` is not
/// something a rendered string has.
[<Emit("(function(d){ window.__typed = (window.__typed || '') + d })($1)")>]
let private recordTyped (_terminal: TerminalId) (data: string) : unit = jsNative

/// Hand the shell a terminal SCREEN, as the Session Process does over the data channel
/// (Plan 14, stage 6). Exposed so the E2E can drive the one path that puts a real emulator
/// in a real browser: without it this bundle contains no xterm at all, and the browser tier
/// silently proved nothing about the client's live screen — which is how a browser-only
/// module-resolution failure got past it and into a release job.
[<Emit("(function(f){ window.__snapshot = (id, seq, screen, cols, rows) => f(id, seq, screen, cols ?? 80, rows ?? 24) })($0)")>]
let private exposeSnapshot (f: string -> int -> string -> int -> int -> unit) : unit = jsNative

/// Hand the shell one transcript record, as the Session Process does as a terminal speaks.
/// The companion to the snapshot: a snapshot is where a screen STARTS and records are what
/// move it, and composing the two — including a resize reshaping the emulator mid-stream —
/// is the client's own fold.
[<Emit("(function(f){ window.__record = f })($0)")>]
let private exposeRecord (f: string -> int -> string -> string -> unit) : unit = jsNative

/// The size this client last told the Session Process its screen is. Read back by the E2E,
/// because the question there is whether a box that changed without the model changing — a
/// splitter dragged, a window resized — reached the pty at all.
[<Emit("(function(c, r){ window.__resized = c + 'x' + r })($1, $2)")>]
let private recordResized (_terminal: TerminalId) (cols: int) (rows: int) : unit = jsNative

/// The size this client last measured its OWN view of a terminal at — the width a command
/// queued from that pane would claim (`PendingAct.Size`). A second hook rather than a reading
/// of `window.__resized`, because the two are different facts: that one is what was sent to the
/// pty for a lease this peer holds, and this one is measured in BLOCK mode, where nobody holds
/// anything and nothing is sent at all.
[<Emit("(function(c, r){ window.__viewport = c + 'x' + r })($1, $2)")>]
let private recordViewport (_terminal: TerminalId) (cols: int) (rows: int) : unit = jsNative

/// Hand a terminal's lease to this peer WITHOUT a press, as the alt-screen flip does: a block
/// takes the screen and the Session Process gives its author the keyboard. Exposed for the
/// same reason the snapshot is — it is the arrival of a fact from elsewhere, and a test that
/// could only reach live mode by pressing `take` could never exercise the route that has no
/// press to make.
[<Emit("(function(f){ window.__take = f })($0)")>]
let private exposeTake (f: string -> unit) : unit = jsNative

do
    dressShell Style.app
    // Taking the keyboard is answered by the Session Process, which appends the lease event
    // and sends it back — so here the harness appends it to the projection itself, through
    // the same fold the real page uses. Without this the one act that puts a screen in front
    // of a keyboard is `ignore` in the harness, and the browser tier cannot reach live mode
    // except for the terminal that was born holding a lease.
    let mutable takeRef : TerminalId -> unit = ignore
    let actions =
        { ViewActions.ssr with
            FocusPane = PaneShell.toPane
            FocusChat = PaneShell.toChatItem
            FocusWatch = PaneShell.toWatchToggle
            RevealBlock = fun id blockId -> PaneShell.revealBlock (TerminalId.value id) (BlockId.value blockId)
            RevealMessage = fun id -> PaneShell.revealMessage (MessageId.value id)
            TakeTerminal = fun id -> takeRef id
            TypeIntoTerminal = recordTyped }
    // The app's own player sync, so a block tab in the harness really plays its recording —
    // which is the point of driving this in a browser rather than asserting a string. The
    // forward reference is the same shape `Browser.fs` uses: the syncer needs dispatch (a
    // rewound cast that plays off its end jumps back to live) and dispatch's render needs
    // the syncer.
    let mutable dispatchRef : ClientMsg -> unit = ignore
    let replays = PaneReplays.create (fun msg -> dispatchRef msg)
    // …and the app's own screen composition, for the same reason: the emulator, the
    // serialization and the fold are the client's, and only a browser runs them.
    let screens = Screens.create (fun msg -> dispatchRef msg) recordResized
    let mutable model = shellModel
    let rec dispatch (msg: ClientMsg) : unit =
        model <- ClientModel.update msg model
        // Read back off the MODEL rather than out of the message: a measurement the reducer
        // refused is not a width anything would claim, and a hook that reported it anyway
        // would say the opposite of what happened.
        match msg with
        | TerminalViewportMsg (terminal, _) ->
            Map.tryFind terminal model.TerminalViewports
            |> Option.iter (fun size -> recordViewport terminal size.Cols size.Rows)
        | _ -> ()
        render ()
    and render () =
        Lit.render (unbox shellHost) (View.view actions model dispatch)
        replays.Sync model
        screens.Sync model
        PaneShell.setOpen model.TerminalsOpen
    dispatchRef <- dispatch
    takeRef <-
        fun id ->
            let taken =
                SessionEvent.TerminalLeaseTaken
                    { TerminalId = id; By = ActorRef.PeerRef model.Peer.PeerId; FromSeq = 0 }
            model <- { model with Terminals = Projection.applyEvent model.Terminals taken }
            render ()
    exposeSnapshot (fun id seq screen cols rows ->
        match TerminalId.create id with
        | Ok terminal -> screens.Snapshot terminal { Seq = seq; Cols = cols; Rows = rows; Screen = screen }
        | Error _ -> ())
    exposeTake (fun id ->
        match TerminalId.create id with
        | Ok terminal -> takeRef terminal
        | Error _ -> ())
    exposeRecord (fun id seq kind data ->
        match TerminalId.create id, TranscriptKind.parse kind with
        | Ok terminal, Some kind ->
            dispatch (TerminalRecordMsg (terminal, seq, { At = 0.0; Kind = kind; Data = data }))
        | _ -> ())
    render ()
    // The shell harness drives the real view, so it gets the real shell plumbing too — a
    // splitter that only worked in the app is a splitter no browser-tier test could reach.
    PaneShell.installPaneResize ()
