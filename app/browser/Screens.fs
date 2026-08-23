module Yession.Browser.Screens

// The live terminal screen, in the browser (Plan 14, stage 6).
//
// A terminal in live mode is running a program that moves the cursor, so what it DISPLAYS is
// a projection of what it emitted — not the stream itself. The client therefore needs an
// emulator, and it uses the SAME one the Session Process does (`@xterm/headless`, driven
// through `Emulator.fs`'s contract): fed the same bytes in the same order AT THE SAME SIZE,
// the two screens cannot disagree, which is the property `TerminalSnapshot` rests on and
// which a second, browser-only renderer would quietly break.
//
// The size is half of that sentence and used to be missing from it. This opened every
// emulator at 80x24 and never resized one, while the Process resized both its pty and its own
// emulator on every viewport report — so a wide pane rewrapped every line at 80 columns and a
// program drawing 40 rows spilled its frame into scrollback, under a comment promising the
// two could not disagree. The snapshot now carries the geometry it was painted at, and a
// resize record reshapes the emulator exactly as it reshaped the pty.
//
// The emulator is a live object, so it lives here rather than in the model. What reaches the
// model is its SERIALIZATION — the same form a snapshot travels in — which the view renders
// through the ANSI spans a block's output already uses.
//
//   snapshot(seq, screen)  ->  reset the emulator to that screen, remember seq
//   record(n > seq)        ->  write it, advance seq
//   after either           ->  serialize and dispatch
//
// Composability comes from the seq, exactly as it does for events: the snapshot may be NEWER
// than its seq (the screen is read after the position is taken), which is the safe direction
// — a record already drawn is drawn again, and drawing it twice is idempotent, whereas a seq
// ahead of the screen would skip a record for ever.

open Fable.Core
open Yession.Domain
open Yession.App
open Yession.SessionProcess
open Yession.Host

/// The holder's viewport size in CHARACTER CELLS, measured from the rendered screen rather
/// than assumed: the pane is one width on a desktop and another on a phone, and the program
/// on the other end lays its screen out to whatever it is told. Measured by putting a known
/// run of characters in the element and reading the box it takes, which is the only way to
/// get a font's advance width without hard-coding one.
[<Emit("""(function (terminalId) {
  const el = document.querySelector('[data-terminal-screen="' + terminalId + '"]')
  if (!el) return null
  const probe = document.createElement('span')
  probe.style.cssText = 'position:absolute;visibility:hidden;white-space:pre'
  probe.textContent = 'M'.repeat(80)
  el.appendChild(probe)
  const cell = probe.getBoundingClientRect().width / 80
  const line = probe.getBoundingClientRect().height
  el.removeChild(probe)
  if (!(cell > 0) || !(line > 0)) return null
  const box = el.getBoundingClientRect()
  const cols = Math.max(1, Math.floor((box.width - 24) / cell))
  const rows = Math.max(1, Math.floor((box.height - 16) / line))
  return [cols, rows]
})($0)""")>]
let private measure (terminalId: string) : (int * int) option = jsNative

/// The screen this peer is typing into, if one is on screen. `tabindex="0"` is what makes it
/// the holder's: the other two variants the view renders are `role="region"`, and the pane
/// shows one tab at a time.
[<Emit("document.querySelector('[data-terminal-screen][tabindex=\"0\"]')")>]
let private holderScreen () : obj = jsNative

[<Emit("new ResizeObserver($0)")>]
let private newResizeObserver (onResized: unit -> unit) : obj = jsNative

[<Emit("$0.observe($1)")>]
let private observe (observer: obj) (element: obj) : unit = jsNative

/// Every target at once, which is all of them: exactly one element is ever observed.
[<Emit("$0.disconnect()")>]
let private disconnect (observer: obj) : unit = jsNative

[<Emit("$0 === $1")>]
let private isSame (a: obj) (b: obj) : bool = jsNative

/// One terminal's screen as this client composes it.
type private Live =
    { Emulator : Emulator
      /// The transcript position the emulator has been fed through.
      mutable Through : int
      /// The last serialization dispatched, so an unchanged screen is not re-dispatched into
      /// a render loop that would then ask for it again.
      mutable Rendered : string }

type Screens =
    { /// The Process's screen for a terminal: the transcript position it represents, and the
      /// size it was painted at.
      Snapshot : TerminalId -> TranscriptKeyframe -> unit
      /// Fold everything the model has that this client's screens have not seen, and
      /// dispatch any that changed. Safe to call after every render: a screen that did not
      /// move dispatches nothing.
      Sync : ClientModel -> unit
      /// Drop a terminal's emulator. An emulator is a live object with a worker behind it;
      /// one left behind for a terminal nobody is in is a leak with no screen.
      Forget : TerminalId -> unit }

/// `report` is told the holder's viewport size whenever it changes — the app relays it to the
/// Session Process, which resizes the pty.
///
/// It lives here, with the screen, rather than in the app beside the connection. The size of a
/// screen is a fact about that screen: the element to measure is the one this already tracks,
/// the moment to measure is a render this already runs after, and the de-duplication is about
/// what this already knows. In the app it was also unreachable — the browser tier drives the
/// harness, which has no copy of it — which is how the one thing it does wrong went unnoticed:
/// it ran only from `setState`, so a splitter drag or a window resize, which change the box
/// and dispatch nothing, never reached the pty at all.
let create (dispatch: ClientMsg -> unit) (report: TerminalId -> int -> int -> unit) : Screens =
    let live = System.Collections.Generic.Dictionary<string, Live> ()

    /// Whether this client held each terminal's lease at the last sync — the other half of the
    /// edge below. Kept apart from `live` on purpose: the lease can land on a terminal whose
    /// snapshot has not arrived, and an edge that only fired for terminals with an emulator
    /// would miss exactly the terminal somebody just opened.
    let held = System.Collections.Generic.Dictionary<string, bool> ()

    /// The size each terminal's viewport was last reported at, so a render that moved nothing
    /// does not re-send it — a resize is a signal to the program on the other end, and
    /// repeating it makes a full-screen program redraw for no reason.
    let reportedSize = System.Collections.Generic.Dictionary<string, int * int> ()

    let forget (key: string) =
        held.Remove key |> ignore
        reportedSize.Remove key |> ignore
        match live.TryGetValue key with
        | true, existing ->
            existing.Emulator.Dispose ()
            live.Remove key |> ignore
        | _ -> ()

    /// Serialize and dispatch, if the screen moved. `Serialize` waits on the emulator's own
    /// write barrier, so this reads a screen that has been drawn rather than one that is
    /// still being parsed.
    let publish (id: TerminalId) (entry: Live) =
        Async.StartImmediate (
            async {
                let! screen = entry.Emulator.Serialize ()
                if screen <> entry.Rendered then
                    entry.Rendered <- screen
                    dispatch (TerminalScreenMsg (id, screen))
            })

    /// The model as of the last sync, so the observer below has something to measure against.
    /// A box changing is not a model change — that is the whole point of watching it — so the
    /// callback cannot be handed one.
    let mutable latest : ClientModel option = None

    /// Tell the app how big the holder's screen is. Only the HOLDER: the pty has one size and
    /// every peer is looking at the same screen, so a viewer with a narrower pane scrolls
    /// rather than reshaping everyone else's terminal.
    let reportSizes (model: ClientModel) =
        let mine = ActorRef.PeerRef model.Peer.PeerId
        for terminal in TerminalProjection.openTerminals model.Terminals do
            if terminal.Lease = Some mine then
                let key = TerminalId.value terminal.TerminalId
                match measure key with
                | None -> ()
                | Some (cols, rows) ->
                    let last = match reportedSize.TryGetValue key with | true, v -> Some v | _ -> None
                    if last <> Some (cols, rows) then
                        reportedSize.[key] <- (cols, rows)
                        report terminal.TerminalId cols rows

    /// One observer, re-pointed at whichever screen the holder is typing into. A box can change
    /// without the model changing — the splitter is dragged, the window is resized, the phone
    /// is turned — and those are exactly the cases a render-loop measurement cannot see.
    ///
    /// `reportedSize` is what keeps this cheap: an observer fires on every frame of a drag, and
    /// only the frames that cross a whole character cell send anything.
    let observer = newResizeObserver (fun () -> latest |> Option.iter reportSizes)
    let mutable observed : obj = null

    let watchHolderScreen () =
        let element = holderScreen ()
        if not (isSame element observed) then
            disconnect observer
            observed <- element
            if not (isNull element) then observe observer element

    { Snapshot =
        fun id keyframe ->
            let key = TerminalId.value id
            // A snapshot is the whole screen, so the emulator starts again from it rather
            // than having it appended: re-seeding an emulator that already holds a screen
            // would draw the old one under the new.
            forget key
            // Opened at the size the screen was PAINTED at, never at the default: a paint is
            // a grid, and repainting it into a narrower one wraps every line that was long
            // enough to matter.
            let emulator = Emulator.openEmulator keyframe.Cols keyframe.Rows
            emulator.Write keyframe.Screen
            let entry = { Emulator = emulator; Through = keyframe.Seq; Rendered = "" }
            live.[key] <- entry
            publish id entry
      Sync =
        fun model ->
            // The keyboard follows the lease. Both ways into live mode — pressing `take`, and
            // the alt-screen flip handing a block's author the terminal it just took over —
            // remove the focused element in the render they arrive on, so without this the
            // person who now owns the keyboard is typing into `body`.
            //
            // Here rather than on the `take` press because the flip has no press to hang it
            // on: it is the Session Process saying the mode changed, which reaches this client
            // as a model change like any other. One edge, both routes.
            let mine = ActorRef.PeerRef model.Peer.PeerId
            let showing = ClientModel.selectedTerminal model
            for terminal in TerminalProjection.openTerminals model.Terminals do
                let key = TerminalId.value terminal.TerminalId
                let isMine = terminal.Lease = Some mine
                let was = match held.TryGetValue key with | true, v -> v | _ -> false
                held.[key] <- isMine
                // Only the terminal the pane is SHOWING: a lease landing on one the reader is
                // not looking at has no screen in the document to focus, and the selector
                // would otherwise find whichever live screen happened to be on it instead.
                if isMine && not was && showing = Some terminal.TerminalId then
                    PaneShell.toTerminalScreen ()
            for terminal in TerminalProjection.openTerminals model.Terminals do
                let key = TerminalId.value terminal.TerminalId
                match live.TryGetValue key with
                // No snapshot yet: nothing to fold onto. The Process sends one when a peer
                // joins a terminal, and this runs again when it lands.
                | false, _ -> ()
                | true, entry ->
                    let feed = ClientModel.terminalFeed terminal.TerminalId model
                    // Resize records are folded alongside the output, in ONE ordered pass:
                    // what a program drew before a resize was drawn at the old geometry and
                    // what it drew after at the new, so applying them out of order — or not
                    // at all — reflows the wrong half of the screen.
                    let fresh =
                        feed.Records
                        |> Map.toList
                        |> List.filter (fun (seq, record) ->
                            seq >= entry.Through
                            && (record.Kind = TranscriptOutput
                                || record.Kind = TranscriptStderr
                                || record.Kind = TranscriptResize))
                    if not (List.isEmpty fresh) then
                        for _, record in fresh do
                            match record.Kind with
                            | TranscriptResize ->
                                // A record nobody here wrote, so a payload this cannot read is
                                // one to skip rather than to fail on.
                                match TerminalSize.parse record.Data with
                                | Some size -> entry.Emulator.Resize size.Cols size.Rows
                                | None -> ()
                            | _ -> entry.Emulator.Write record.Data
                        entry.Through <- (fresh |> List.map fst |> List.max) + 1
                        publish terminal.TerminalId entry
            // A terminal that closed keeps neither a screen nor an emulator.
            let open' =
                TerminalProjection.openTerminals model.Terminals
                |> List.map (fun t -> TerminalId.value t.TerminalId)
                |> Set.ofList
            for stale in
                Seq.append live.Keys held.Keys
                |> Seq.filter (fun k -> not (Set.contains k open'))
                |> Seq.distinct
                |> Seq.toList do
                forget stale
            // Last, because both read the document this render has just produced: which screen
            // the holder is typing into, and how big it is.
            latest <- Some model
            watchHolderScreen ()
            reportSizes model
      Forget = fun id -> forget (TerminalId.value id) }
