module Yession.Browser.LongPress

// Hold a finger on a message and its actions menu opens — the touch counterpart of hovering
// it and pressing the ellipsis.
//
// It exists because the ellipsis is REVEALED by hover, and a device with no pointer never
// hovers. It is on the screen there at half strength so it can still be tapped, but a 24px
// target beside a paragraph is not what a thumb reaches for; the message itself is.
//
// One DELEGATED listener rather than a binding per item. The conversation is a list the
// length of a session, Lit re-renders it on every frame that changes anything, and per-item
// handlers would be added and removed by the thousand for a gesture that can only be
// happening in one place at a time. `closest` answers which message a press was inside, on
// the one press where it matters.
//
// The gesture, and every way it has to STOP being one:
//
//   pointerdown   a touch or a pen inside a message starts the clock and remembers where
//   pointermove   past a few pixels it is a scroll or a selection, not a hold
//   pointerup     a tap
//   pointercancel the platform took the gesture (a scroll it decided to own)
//   scroll        the list moved under a finger that never moved itself
//   selectstart   the platform is selecting text with it, and text wins
//
// A mouse is excluded rather than handled: it has the ellipsis, and a mouse held still on a
// message for half a second is somebody reading.

open Browser.Dom
open Browser.Types
open Fable.Core

/// How long a press has to last. Long enough not to fire on a tap that lingers, short enough
/// to feel like a decision rather than a wait — the interval every platform's own long press
/// uses, and the one a thumb is already calibrated to.
let private holdMs = 500.0

/// How far a finger may drift and still be a hold. A finger never rests: this is the slack
/// that separates "holding still" from "beginning to scroll", not a tolerance for aiming.
let private driftPx = 10.0

[<Emit("$0.pointerType")>]
let private pointerType (e: Event) : string = jsNative

[<Emit("$0.clientX")>]
let private clientX (e: Event) : float = jsNative

[<Emit("$0.clientY")>]
let private clientY (e: Event) : float = jsNative

/// The message an event happened inside, if any. `closest` from the target, so a press on the
/// prose, on a chip, or on the item's own padding all answer the same message — and a press
/// on the menu's backdrop answers nothing, which is what keeps a hold from starting while a
/// menu is already open.
[<Emit("(function (e) { const el = e.target && e.target.closest && e.target.closest('[data-message-id]'); return el ? el.getAttribute('data-message-id') : null })($0)")>]
let private messageUnder (e: Event) : string = jsNative

/// Watch the document for a press held on a message.
///
/// `opened` is called with the message's id, once per hold. Everything after that is the
/// model's: this reports a gesture, it does not know what a menu is.
let watch (opened: string -> unit) : unit =
    // The hold in flight, as the id it would open and where the finger went down. `None`
    // between gestures, which is also what every cancel below writes.
    let mutable pending : (string * float * float) option = None
    let mutable timer = 0.0
    // A hold that FIRED. The platform sends a click after the finger lifts and, on Android, a
    // `contextmenu` as well — both belonging to a gesture that has already been answered, and
    // the click would land on whatever is now under the finger, which is the menu that just
    // opened.
    let mutable fired = false

    let cancel () =
        if timer <> 0.0 then
            window.clearTimeout timer
            timer <- 0.0
        pending <- None

    window.document.addEventListener (
        "pointerdown",
        (fun e ->
            // A mouse has the ellipsis, and a mouse held still is somebody reading.
            if pointerType e <> "mouse" then
                match messageUnder e with
                | null -> ()
                | messageId ->
                    cancel ()
                    fired <- false
                    pending <- Some (messageId, clientX e, clientY e)
                    timer <-
                        window.setTimeout (
                            (fun () ->
                                timer <- 0.0
                                match pending with
                                | Some (messageId, _, _) ->
                                    pending <- None
                                    fired <- true
                                    opened messageId
                                | None -> ()),
                            int holdMs)),
        true)

    window.document.addEventListener (
        "pointermove",
        (fun e ->
            match pending with
            | Some (_, x, y) when abs (clientX e - x) > driftPx || abs (clientY e - y) > driftPx -> cancel ()
            | _ -> ()),
        true)

    for ending in [ "pointerup"; "pointercancel" ] do
        window.document.addEventListener (ending, (fun _ -> cancel ()), true)

    // A list that moves under a finger which never moved itself: the pointer's coordinates
    // are unchanged, so nothing above sees it. Capture, because the scroller is a descendant
    // and scroll does not bubble.
    window.document.addEventListener ("scroll", (fun _ -> cancel ()), true)

    // And the one that is not a cancel so much as a CONCESSION. Every platform binds its own
    // long press on text to selecting that text, at about the same half second this waits —
    // so a finger held on a paragraph is two gestures at once, and the reader only ever meant
    // one of them. Whichever this is, the platform knows first: `selectstart` is it saying so.
    //
    // Text wins, and not as a tie-break. Selecting what somebody said is a thing every page
    // can do and this one was quietly taking away; opening the menu is a shortcut to a control
    // already on the screen, which is where a device with no pointer reaches for it anyway
    // (`Style.itemActions` keeps the ellipsis visible at half strength there). What is left
    // for the hold is the rest of the row — the gutter, the padding, the space past a short
    // line — which on a phone is most of it, because the ground runs edge to edge.
    //
    // Left unyielded, this did not merely open a menu over a selection. The menu's backdrop is
    // a `fixed inset-0` element, so a hold on a paragraph put a viewport-sized box under the
    // finger at the exact moment the platform was deciding what the selection covered, and it
    // answered: the whole screen.
    window.document.addEventListener ("selectstart", (fun _ -> cancel ()), true)

    // The two events a fired hold leaves behind. Suppressed rather than ignored: the click
    // would land on the menu this gesture just opened, and the platform's own menu would
    // arrive over it offering to copy a link.
    window.document.addEventListener (
        "click",
        (fun e ->
            if fired then
                fired <- false
                e.preventDefault ()
                e.stopPropagation ()),
        true)

    window.document.addEventListener (
        "contextmenu",
        (fun e -> if fired then e.preventDefault ()),
        true)
