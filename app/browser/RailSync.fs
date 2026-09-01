module Yession.Browser.RailSync

// The landmark rail's other half: where `Rail` says a stroke goes, this says what its inputs
// are. Measuring is all it does — the rail's box, and the top of every marked message — and
// then it writes one number per stroke as a custom property the stylesheet positions from.
//
// A custom property rather than a model field for the ordinary reason a pixel is never a
// fact about a session: the number changes on every scroll frame, and a model that carried it
// would re-render the whole conversation sixty times a second to move eight hairlines.
//
// Its own module, and before `Browser.fs`, because two entry points drive it: the app and the
// host-free shell harness the `Browser`-tier E2E runs against. A second copy would be a
// second thing to keep correct, and the one that rotted would be the one nothing runs.

open Browser.Dom
open Browser.Types
open Fable.BrowserExtras
open Yession.App

/// The least two hairlines can be apart and still read as two. Only ever reached outside the
/// exact zone: inside it the conversation has already spread the marks out, and anything this
/// close together there is two messages genuinely touching.
let [<Literal>] private Gap = 6.0

let private find (selector: string) : HTMLElement option =
    match document.querySelector selector with
    | null -> None
    | element -> Some (element :?> HTMLElement)

let private strokesIn (rail: HTMLElement) : HTMLElement list =
    let found = rail.querySelectorAll "[data-landmark]"
    [ for i in 0 .. found.length - 1 -> found.[i] :?> HTMLElement ]

/// Place every stroke against the conversation as it stands right now.
///
/// Synchronous, and called at the END of a render rather than a frame later like the acts in
/// `PaneShell`: those move focus into markup that has to exist first, this measures markup Lit
/// has just written. Deferring it would paint one frame with every stroke at the default the
/// stylesheet names, which on a rail is eight hairlines stacked at the bottom.
///
/// A stroke whose message is not in the document keeps whatever it had. That cannot happen
/// today — the view renders every item and marks only exist on items — and the alternative is
/// inventing a position for a message nobody can see.
let sync () : unit =
    find "[data-landmark-rail]"
    |> Option.iter (fun rail ->
        let box = rail.getBoundingClientRect ()
        let measured =
            strokesIn rail
            |> List.choose (fun stroke ->
                let selector =
                    sprintf "[data-conversation] [data-message-id=\"%s\"]" (stroke.getAttribute "data-landmark")
                find selector
                |> Option.map (fun item ->
                    stroke, Rail.place box.height (box.bottom - (item.getBoundingClientRect ()).top)))
        Rail.spaced Gap box.height (measured |> List.map snd)
        |> List.iter2 (fun (stroke, _) place -> setStyleProperty stroke "--rail-at" (sprintf "%.2fpx" place)) measured)

/// Follow the conversation while it moves under the rail.
///
/// Two things move a stroke without changing the model: scrolling the timeline, and resizing
/// the window under a laid-out one. Both are answered by measuring again, and both can arrive
/// far faster than a frame — so they only ever ASK for a measurement, and a frame later at
/// most one happens.
///
/// The scroll listener is on the document in the CAPTURE phase because scroll events do not
/// bubble, and because the element they fire on is re-rendered constantly: a listener bound to
/// the timeline would have to be bound and unbound with every frame, and the frame it was
/// missing from is the one the reader is scrolling in.
let watch () : unit =
    let pending = ref false
    let settle () =
        if not pending.Value then
            pending.Value <- true
            window.requestAnimationFrame (fun _ ->
                pending.Value <- false
                sync ())
            |> ignore
    document.addEventListener ("scroll", (fun _ -> settle ()), true)
    window.addEventListener ("resize", fun _ -> settle ())
