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
open Yession.App

[<Emit("document.getElementById('host')")>]
let private host : obj = jsNative

/// The replay mount (Plan 13, stage 3e). It shares this page rather than getting one of its
/// own because it is the same KIND of thing — a host-free surface that needs a real browser
/// and nothing else — and a second harness would be a second bundle, a second page and a
/// second static server for one `create` call.
[<Emit("document.getElementById('replay')")>]
let private replayHost : Browser.Types.Element = jsNative

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
        TranscriptReplay.cast
            { Width = 80; Height = 24; Timestamp = 0L }
            [ 0, { At = 0.0; Kind = TranscriptInput; Data = "ls -la\r\n" }
              1, { At = 0.1; Kind = TranscriptOutput; Data = "total 0\r\n" } ]
    // Markers and a poster ride the same mount (Plan 14, stage 4): they are the player's own
    // options, and whether they resolve through this bundle is the same question the import
    // itself is. A `startAt` is deliberately absent here — it would skip past the very frame
    // the replay assertion below waits for.
    Replay.mount
        replayHost
        { Cast = cast
          Markers = [ 0.0, "ls -la" ]
          StartAt = None
          Poster = None
          BehindLive = None }
        None
    |> ignore

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
    let blockId : BlockId = BlockId.create "block-harness" |> expect
    let peerId : PeerId = PeerId.create "ada" |> expect
    let messageId : MessageId = MessageId.create "msg-harness" |> expect
    let offset (n: int64) : EventOffset = EventOffset.create n |> expect
    { ClientModel.init { PeerId = peerId; DisplayName = "swift-heron" } with
        Connection = Connected
        Session = Some (SessionId.create "harness" |> expect)
        Conversation =
            { Items =
                [ { MessageId = messageId
                    Author = PeerRef peerId
                    Body = "ship it"
                    Status = Complete
                    Kind = ConversationItemKind.Message
                    Offset = offset 1L } ]
              ActiveAgentMessages = Map.empty }
        Timeline = { TimelineProjection.empty with TerminalItems = [ TimelineBlock (offset 2L, terminalId, blockId) ] }
        Terminals =
            { Terminals =
                [ { TerminalId = terminalId
                    Title = "build"
                    OpenedBy = PeerRef peerId
                    Sandbox = SandboxName.defaultName
                    IsOpen = true
                    ClosedReason = None
                    Lease = None
                    IntegrationLost = false
                    Blocks =
                      [ { BlockId = blockId
                          QueueId = None
                          Author = PeerRef peerId
                          ApprovedBy = None
                          Command = "ls -la"
                          FromSeq = 0
                          ToSeq = Some 2
                          Status = BlockFinished (CommandSucceeded 0) } ]
                    DroppedBytes = 0 }
                  { TerminalId = liveId
                    Title = "shell"
                    OpenedBy = PeerRef peerId
                    Sandbox = SandboxName.defaultName
                    IsOpen = true
                    ClosedReason = None
                    Lease = Some (PeerRef peerId)
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
                    Header = Some { Width = 80; Height = 24; Timestamp = 0L } } ]
        // SHUT to begin with, like a fresh client: the phone case is about what happens when
        // a chip brings the pane on screen, which is nothing to watch if it is already there.
        TerminalScreens = Map.ofList [ liveId, "\u001b[32mvim ~/notes\u001b[0m" ]
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
[<Emit("(function(f){ window.__snapshot = f })($0)")>]
let private exposeSnapshot (f: string -> int -> string -> unit) : unit = jsNative

do
    dressShell Style.app
    let actions =
        { ViewActions.ssr with
            FocusPane = PaneShell.toPane
            FocusChat = PaneShell.toChatItem
            FocusDvr = fun id -> PaneShell.toDvrControl (TerminalId.value id)
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
    let screens = Screens.create (fun msg -> dispatchRef msg)
    let mutable model = shellModel
    let rec dispatch (msg: ClientMsg) : unit =
        model <- ClientModel.update msg model
        render ()
    and render () =
        Lit.render (unbox shellHost) (View.view actions model dispatch)
        replays.Sync model
        screens.Sync model
        PaneShell.setOpen model.TerminalsOpen
    dispatchRef <- dispatch
    exposeSnapshot (fun id seq screen ->
        match TerminalId.create id with
        | Ok terminal -> screens.Snapshot terminal seq screen
        | Error _ -> ())
    render ()
