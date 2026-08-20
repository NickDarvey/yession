module Yession.Browser.PaneReplays

// Keeping the mounted players in step with the model (Plan 13, stage 3e; Plan 14, stage 4).
//
// The view renders empty `[data-pane-replay="<tab key>"]` hosts and this attaches a player to
// each, because a `.cast` is a whole artefact rather than a value to bind and the player owns
// its own DOM once mounted. ONE mount path for all three kinds of recording — a whole
// terminal, a block's range, a stretch's — because they differ in what they play rather than
// in how they are mounted; the model turns the tab's key back into what to play.
//
// Its own module because two entry points drive it: the app (`Browser.fs`) and the host-free
// shell harness the `Browser`-tier E2E runs. A second copy would be a second thing to keep
// correct, and the one that rotted would be the one nothing runs.

open Fable.Core
open Yession.Domain
open Yession.App

[<Emit("Array.from(document.querySelectorAll('[data-pane-replay]'))")>]
let private mounts () : obj[] = jsNative

[<Emit("$0.getAttribute('data-pane-replay')")>]
let private mountKey (el: obj) : string = jsNative

[<Emit("$0.childElementCount > 0")>]
let private isMounted (el: obj) : bool = jsNative

[<Emit("(function (el) { while (el.firstChild) el.removeChild(el.firstChild) })($0)")>]
let private clearChildren (el: obj) : unit = jsNative

/// Drive this from the render loop, after every render.
type Syncer = { Sync : ClientModel -> unit }

/// `dispatch` feeds the one message a mounted player raises on its own: a rewound cast that
/// played off its end has caught the reader up (Plan 14, stage 7), and the answer is a jump
/// back to live rather than a stale final frame.
let create (dispatch: ClientMsg -> unit) : Syncer =
    /// Mounted players by tab key, each with the cast text it was mounted over — so a
    /// recording that arrived in pieces can be told apart from one that has not changed.
    let players = System.Collections.Generic.Dictionary<string, Replay.Mounted * string> ()

    let mount (model: ClientModel) (el: obj) (key: string) =
        match ClientModel.paneTabs model |> List.tryFind (fun t -> PaneTab.key t = key) with
        | None -> ()
        | Some tab ->
            // `None` means the recording is not ready to play — chunk 0 has not arrived, or
            // the range has no end yet. The next render runs this again.
            match ClientModel.paneReplay tab model with
            | None -> ()
            | Some replay ->
                let caughtUp =
                    replay.BehindLive
                    |> Option.map (fun terminal () ->
                        dispatch (JumpToLiveMsg terminal)
                        PaneShell.toDvrControl (TerminalId.value terminal))
                players.[key] <- (Replay.mount (unbox el) replay caughtUp, replay.Cast)

    { Sync =
        fun model ->
            let live = System.Collections.Generic.HashSet<string> ()
            for el in mounts () do
                let key = mountKey el
                if not (isNull (box key)) && key <> "" then
                    live.Add key |> ignore
                    // Keyed on the mount being EMPTY rather than on a model flag: the view
                    // re-renders for reasons that have nothing to do with this recording, and
                    // re-mounting on every render would restart it under whoever is watching.
                    if not (isMounted el) then mount model el key
            // A recording can still arrive in pieces — the chunk that carries the header, the
            // keyframe fetched beside it — and a player mounted over the earlier answer is
            // playing the wrong thing. So it is rebuilt when what it should play changes, and
            // every cast this mounts is a CLOSED range, so that happens once or twice and
            // then never again.
            for KeyValue (key, (_, cast)) in players |> Seq.toList do
                match ClientModel.paneTabs model |> List.tryFind (fun t -> PaneTab.key t = key) with
                | Some tab ->
                    match ClientModel.paneReplay tab model with
                    | Some replay when replay.Cast <> cast ->
                        match mounts () |> Array.tryFind (fun el -> mountKey el = key) with
                        | Some el ->
                            (fst players.[key]).Dispose ()
                            players.Remove key |> ignore
                            clearChildren el
                            mount model el key
                        | None -> ()
                    | _ -> ()
                | None -> ()
            // A player whose mount is gone keeps a worker alive; take it down with the node.
            for stale in players.Keys |> Seq.filter (fun k -> not (live.Contains k)) |> Seq.toList do
                (fst players.[stale]).Dispose ()
                players.Remove stale |> ignore }
