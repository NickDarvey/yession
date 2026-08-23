module Yession.Browser.Screens

// The live terminal screen, in the browser (Plan 14, stage 6).
//
// A terminal in live mode is running a program that moves the cursor, so what it DISPLAYS is
// a projection of what it emitted — not the stream itself. The client therefore needs an
// emulator, and it uses the SAME one the Session Process does (`@xterm/headless`, driven
// through `Emulator.fs`'s contract): fed the same bytes in the same order, the two screens
// cannot disagree, which is the property `TerminalSnapshot` already rests on and which a
// second, browser-only renderer would quietly break.
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

/// One terminal's screen as this client composes it.
type private Live =
    { Emulator : Emulator
      /// The transcript position the emulator has been fed through.
      mutable Through : int
      /// The last serialization dispatched, so an unchanged screen is not re-dispatched into
      /// a render loop that would then ask for it again.
      mutable Rendered : string }

type Screens =
    { /// The Process's screen for a terminal, with the transcript position it represents.
      Snapshot : TerminalId -> int -> string -> unit
      /// Fold everything the model has that this client's screens have not seen, and
      /// dispatch any that changed. Safe to call after every render: a screen that did not
      /// move dispatches nothing.
      Sync : ClientModel -> unit
      /// Drop a terminal's emulator. An emulator is a live object with a worker behind it;
      /// one left behind for a terminal nobody is in is a leak with no screen.
      Forget : TerminalId -> unit }

let create (dispatch: ClientMsg -> unit) : Screens =
    let live = System.Collections.Generic.Dictionary<string, Live> ()

    /// Whether this client held each terminal's lease at the last sync — the other half of the
    /// edge below. Kept apart from `live` on purpose: the lease can land on a terminal whose
    /// snapshot has not arrived, and an edge that only fired for terminals with an emulator
    /// would miss exactly the terminal somebody just opened.
    let held = System.Collections.Generic.Dictionary<string, bool> ()

    let forget (key: string) =
        held.Remove key |> ignore
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

    { Snapshot =
        fun id seq screen ->
            let key = TerminalId.value id
            // A snapshot is the whole screen, so the emulator starts again from it rather
            // than having it appended: re-seeding an emulator that already holds a screen
            // would draw the old one under the new.
            forget key
            let size = TerminalSize.default'
            let emulator = Emulator.openEmulator size.Cols size.Rows
            emulator.Write screen
            let entry = { Emulator = emulator; Through = seq; Rendered = "" }
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
                    let fresh =
                        feed.Records
                        |> Map.toList
                        |> List.filter (fun (seq, record) ->
                            seq >= entry.Through
                            && (record.Kind = TranscriptOutput || record.Kind = TranscriptStderr))
                    if not (List.isEmpty fresh) then
                        for _, record in fresh do entry.Emulator.Write record.Data
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
      Forget = fun id -> forget (TerminalId.value id) }

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
let measure (terminalId: string) : (int * int) option = jsNative
