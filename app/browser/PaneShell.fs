module Yession.Browser.PaneShell

// The bits of the pane that the model drives but a Lit render cannot do: moving focus, and
// the root class the column's open state is expressed as (Plan 13; Plan 14, stages 2 and 5).
//
// The chat and the pane are two columns, and tapping a chip in one puts something new in the
// other. Focus has to follow, or a keyboard user presses Enter on a chip and stays exactly
// where they were with no way of knowing anything happened. Closing that tab has the mirror
// problem: the control that was focused leaves the document.
//
// Its own module because two entry points need all of it — the app (`Browser.fs`) and the
// host-free shell harness the `Browser`-tier E2E drives. A second copy would be a second
// thing to keep correct, and the one that rotted would be the one nothing runs.
//
// Everything here waits a frame: the model changes first, Lit renders second, and `focus()`
// on an element that is not in the document yet is a no-op.
//
// This file used to be `[<Emit>]` bodies — a hundred-odd lines of JavaScript in strings, and
// most of it POLICY rather than binding: which element is stranded, how a tab key becomes a
// selector, what the splitter's bounds are. Emit bodies are inlined into whatever calls them,
// and Fable does not treat a change to one as a change to its callers, so editing this file
// silently left every compiled caller stale — a test failing for a reason its source does not
// contain, three runs of the browser tier before anybody looked at the bundle. Ordinary F# has
// none of that: it is a module, so what depends on it recompiles. What is genuinely a binding
// went to `Fable.BrowserExtras`; the rest is `Browser.Dom`, typed, and readable.

open Browser.Dom
open Browser.Types
open Browser.WebStorage
open Fable.BrowserExtras

/// A frame later, which is when the render that has to have happened, has. Every act in this
/// module is "the model already changed, now do the part the DOM owns", and every one of them
/// needs the element to be in the document before it can be touched.
let private nextFrame (act: unit -> unit) : unit =
    window.requestAnimationFrame (fun _ -> act ()) |> ignore

/// The first element matching, as something focusable. Everything here selects by `data-*`
/// attributes the view puts on real controls, so the cast is the view's contract rather than
/// an assumption: a hook on something unfocusable would be the bug, not this.
let private find (selector: string) : HTMLElement option =
    match document.querySelector selector with
    | null -> None
    | element -> Some (element :?> HTMLElement)

let private focusOn (element: HTMLElement option) : unit =
    element |> Option.iter (fun element -> element.focus ())

/// Make the browser flush layout, by reading something it can only answer by doing so. The
/// value is thrown away and the READ is the point, which is a thing to say out loud because it
/// looks exactly like a line that could be deleted. (esbuild keeps it, minified or not: a
/// property access may be a getter, so it is never assumed pure.)
let private reflow (element: HTMLElement) : unit = element.offsetWidth |> ignore


/// A CSS length as a number, with the unit dropped. `getPropertyValue` answers `"420px"` for a
/// property set in pixels and `""` for one that is not set at all, and both have to become "no
/// number I can use" rather than a parse that quietly succeeds at zero.
let private trimPx (value: string) : string = value.Trim().Replace ("px", "")

/// Whether focus has been left nowhere it can act from — on `body`, or nowhere at all — or
/// inside something that is on its way out of the document.
///
/// This is the guard that lets a focus move run from the render loop rather than from a press.
/// A press knows it is about to remove the thing under the hand; a render does not, and a
/// render that moved focus unconditionally would yank a caret out of whatever somebody was
/// typing in the moment something unrelated changed elsewhere on the page.
let private stranded (leaving: string list) : bool =
    match document.activeElement with
    | null -> true
    | active ->
        System.Object.ReferenceEquals (active, document.body)
        || leaving |> List.exists (fun selector -> (active.closest selector).IsSome)

/// Move focus into the side pane after a chip opened a tab there.
let toPane () : unit = nextFrame (fun () -> focusOn (find "[data-pane-panel]"))

/// Return focus to the chat item that opened a tab, given that tab's key — the only thing the
/// chip and the tab share. Falls back to the strip's first tab when the item has scrolled out
/// of the rendered chat, because focus has to land somewhere real.
let toChatItem (tabKey: string) : unit =
    nextFrame (fun () ->
        let parts = tabKey.Split ':'
        let back =
            match List.ofArray parts with
            | "block" :: _ :: blockId :: _ -> find (sprintf "[data-chat-block=\"%s\"]" blockId)
            | "stretch" :: rest when not (List.isEmpty rest) ->
                find (sprintf "[data-chat-stretch=\"%s\"]" (String.concat ":" rest))
            | _ -> None
        back
        |> Option.orElseWith (fun () -> find "[role=\"tablist\"] [role=\"tab\"]")
        |> focusOn)

/// Hand focus to a terminal's watch toggle when the reader has been stranded (Plan 14,
/// stage 7; Plan 25, stage 3).
///
/// There used to be four controls here — rewind, jump-to-live, play, back-to-blocks — each
/// swapping another out of the document, so every press risked stranding focus and this had
/// to name all four to catch whichever had survived. One toggle that relabels in place needs
/// none of that: a press keeps its own focus.
///
/// What is left is the case no press causes. A rewound cast playing off its end unmounts the
/// player by itself, under whoever was reading it.
///
/// No terminal id: the toggle's VALUE is the face it will show, not which terminal it is
/// about, and the pane shows one tab at a time — so there is exactly one of these in the
/// document and naming a terminal could only ever name it wrongly.
let toWatchToggle () : unit =
    nextFrame (fun () ->
        if stranded [ "[data-pane-replay]" ] then focusOn (find "[data-terminal-watch]"))

/// Hand focus to the live screen when this peer has just become the one typing into it.
///
/// Taking the keyboard is the whole of what live mode IS, and until this the keyboard did not
/// follow it. Both routes in remove the focused element in the same render they arrive on:
/// pressing `take` removes the `take` button, and the lease landing replaces the command line
/// with the lease bar — so whichever of the two had focus is gone and focus falls to `body`.
/// The person then types into nothing, which is indistinguishable from a terminal that does
/// not work.
///
/// The guard is what makes this safe to run from the render loop: a lease can land on a
/// terminal while its holder is reading somewhere else entirely (the alt-screen flip follows
/// the block's AUTHOR, and the agent's blocks flip too), and yanking a caret out of the
/// message composer because a terminal three tabs away went full-screen would be worse than
/// the stranding it fixes.
///
/// `tabindex="0"` in the selector rather than the terminal's id: the screen renders in three
/// variants and only the holder's takes keystrokes, so the focusable one is the only one this
/// could ever mean. The pane shows one tab at a time, which is what makes that unambiguous —
/// the same reason `toWatchToggle` names no terminal either.
let toTerminalScreen () : unit =
    nextFrame (fun () ->
        if stranded [] then focusOn (find "[data-terminal-screen][tabindex=\"0\"]"))

/// Scroll a terminal's history to one of its commands, and say which one (Plan 25, stage 3).
///
/// The other half of "show in terminal": the model moves the reader's POSITION to that block,
/// and this is the part a rendered string cannot do. One shot, from the press — not from the
/// render — because a reveal repeated on every render would fight the reader's own scrolling
/// the moment a record arrived.
///
/// It runs a frame late for the reason everything else here does (the element has to exist),
/// and that frame is also what puts it after `restoreSurfaceScroll`, which returns a freshly
/// rendered scrollback to its end. Landing after it is the whole trick: the reveal wins once,
/// and every render afterwards samples the position the reader was left at, so the two never
/// fight.
///
/// The mark is an animation that ends. A block scrolled to in a wall of identical mono is
/// still a block nobody can pick out; a permanent highlight would still be pointing at it
/// long after the reader had moved on.
let revealBlock (terminalId: string) (blockId: string) : unit =
    nextFrame (fun () ->
        let scrollback =
            find (sprintf "[data-terminal-scrollback][data-terminal-id=\"%s\"]" terminalId)
        let block =
            scrollback
            |> Option.bind (fun scrollback ->
                match scrollback.querySelector (sprintf "[data-terminal-block=\"%s\"]" blockId) with
                | null -> None
                | block -> Some (block :?> HTMLElement))
        block
        |> Option.iter (fun block ->
            block.scrollIntoView ()
            // Restart the animation rather than add a class that is already there: removing
            // it, forcing a reflow by READING a layout property, and adding it back is the
            // only way to replay a CSS animation on an element that has already run it. The
            // read is the load-bearing line, which is why it is not `ignore` on a call that
            // does something — nothing is computed here, the browser is being made to flush.
            // `-line` and not the flash a MESSAGE gets: that one lights a padded ground and
            // rims it, and a block has neither — it is a bare `flex flex-col`, so an edge
            // would be drawn hard against the command's own glyphs.
            block.classList.remove [| "animate-reveal-line" |]
            reflow block
            block.classList.add [| "animate-reveal-line" |]))

/// Scroll the conversation to one message and flash it — the rail's half of "take me back
/// there", and deliberately the same two moves `revealBlock` makes: scroll it into view, then
/// replay the reveal animation so the eye can find which line moved. A jump that only scrolled
/// would leave a reader looking at a screen of text with no idea which of it they asked for.
let revealMessage (messageId: string) : unit =
    nextFrame (fun () ->
        find (sprintf "[data-conversation] [data-message-id=\"%s\"]" messageId)
        |> Option.iter (fun item ->
            // To the MIDDLE, unlike `revealBlock` above: this scrollport pins the author
            // line at its top, so aligning to the start puts the message under the very line
            // that says who said it. `Scrolling.scrollIntoMiddle` carries the rest of why.
            scrollIntoMiddle item
            // Removing, forcing a reflow by READING a layout property, and adding it back is
            // the only way to replay an animation an element has already run. See
            // `revealBlock`, whose comment this is the other half of.
            item.classList.remove [| "animate-reveal" |]
            reflow item
            item.classList.add [| "animate-reveal" |]
            // Take the reader's CURSOR there, not just their eye — a keyboard reader who asked
            // to be taken to a message and was left with focus on the control that scrolled
            // away has been shown the message and stranded away from it. The rail and the reply
            // ref both come through here, so both move focus by this one line. The article is
            // already centred and visible above, so `focus()` finds nothing to scroll and does
            // not fight that placement; `tabindex="-1"` is what lets it land on an article at
            // all — focusable on purpose, never a Tab stop.
            item.focus ()))

/// Put focus back on one item's actions control, once the menu it opened has gone.
///
/// A frame later like every act here, and for the sharper reason: the control is what the
/// menu was hanging off, and the render that removed the menu is the one that has to have
/// happened before this can find it. Nothing to fall back to if it cannot — the item has
/// scrolled out of the window this client holds, and there is no second right place for a
/// cursor that was inside a menu about it.
let toItemActions (messageId: string) : unit =
    nextFrame (fun () -> focusOn (find (sprintf "[data-item-actions=\"%s\"]" messageId)))

/// The pane's open state, as a class on the shell root — the same mechanism the sidebar uses,
/// so a Lit re-render never fights the CSS transition. A `set` rather than a toggle, because
/// the model holds the bit and this only reflects it: the app opens this column itself
/// whenever a chip or a tab is chosen.
let setOpen (isOpen: bool) : unit =
    if isOpen then document.documentElement.classList.remove [| "term-closed" |]
    else document.documentElement.classList.add [| "term-closed" |]

/// The pane's width on desktop, as a custom property on the shell root — the same mechanism
/// the open state uses, and for the same reasons: it is presentation, a Lit re-render must not
/// fight it, and the model has no business holding a number of pixels.
///
/// The column was a fixed 420px chosen as "the width the content actually has", and measured
/// against what a terminal actually prints it is 20 columns short of 80. Rather than guess a
/// better constant for every screen, the split moves and is remembered.
module private Split =

    /// Where a reader's chosen width survives a reload. Per browser profile, like the peer id:
    /// it is a preference about this screen, not a fact about the session.
    let key = "yession:term-width"

    /// Neither column can be dragged away to nothing. The pane's floor is its own; the chat's
    /// is what bounds the pane's ceiling.
    let minPane = 320.0
    let minChat = 420.0

    let private root = document.documentElement

    let private handles () : HTMLElement list =
        let found = document.querySelectorAll "[data-term-resize]"
        [ for i in 0 .. found.length - 1 -> found.[i] :?> HTMLElement ]

    /// The ceiling is what the CHAT can spare, not what the window is: the sidebar takes 280px
    /// of the window and can be collapsed, so a bound measured against `innerWidth` let the pane
    /// grow to 932px on a 1440 screen and left the conversation 228px — its title truncated to a
    /// single letter and its commands gone. Ask the two columns how wide they actually are.
    let widest () : float =
        match find "[data-terminal-panel]", find "[data-conversation]" with
        | Some pane, Some chat ->
            let spare =
                pane.getBoundingClientRect().width + chat.getBoundingClientRect().width - minChat
            max minPane spare
        | _ -> max minPane (window.innerWidth - minChat)

    /// Set the split, clamped, and tell everything that reports it. The separator's
    /// `aria-valuenow` is a value assistive technology reads out, so it is written here rather
    /// than left at whatever literal the template shipped.
    let apply (width: float) : unit =
        // Rounded before clamping, so a bound is a bound exactly: clamping a fraction first
        // and rounding after could land a pixel outside one.
        let next = width |> round |> min (widest ()) |> max minPane
        setStyleProperty root "--term-w" (sprintf "%dpx" (int next))
        for handle in handles () do
            handle.setAttribute ("aria-valuenow", string (int next))
            handle.setAttribute ("aria-valuemin", string (int minPane))
            handle.setAttribute ("aria-valuemax", string (int (widest ())))
        // Storage is denied in a private window, and a split that cannot be remembered is
        // still a split that works.
        try localStorage.setItem (key, string (int next)) with _ -> ()

    /// Where the split is now, asked of the property rather than of the column — because the
    /// column ANIMATES, and a measurement taken mid-transition is not a width anybody chose.
    let current () : float =
        match System.Double.TryParse (styleProperty root "--term-w" |> trimPx) with
        | true, said when said > 0.0 -> said
        | _ -> find "[data-terminal-panel]" |> Option.map (fun pane -> pane.getBoundingClientRect().width) |> Option.defaultValue minPane

    /// The width to start at: what was remembered, else the design token, else the floor.
    ///
    /// Seeded at install ALWAYS — not only when a width was remembered. Unseeded, `current`
    /// had to fall back to measuring the column, and asked while the column is opening it
    /// answers 1px (a shut pane is its own left border) or whatever the easing has reached, so
    /// the arrow keys then step from a number that was never the split.
    let seed () : float =
        let remembered =
            try
                match System.Double.TryParse (localStorage.getItem key) with
                | true, width when width > 0.0 -> Some width
                | _ -> None
            with _ -> None
        let token =
            match
                System.Double.TryParse
                    (computedProperty root "--spacing-term" |> trimPx)
                with
            | true, width when width > 0.0 -> Some width
            | _ -> None
        remembered |> Option.orElse token |> Option.defaultValue minPane

/// Install the splitter: the pane's width becomes something the reader sets, with a pointer or
/// with the keyboard, and keeps.
///
/// Installed once, delegated from the document so it survives every re-render of the handle.
/// The handle is a `separator` with a value, so the arrow keys have to move it — a splitter
/// that only answers a drag is a control a keyboard cannot reach at all.
let installPaneResize () : unit =
    // Which handle an event happened on, asked as "does a handle contain this" rather than by
    // testing the target's type: `contains` answers false for anything that is not a node, so
    // there is nothing to narrow and no way to be wrong about what a target is.
    let handleUnder (target: EventTarget) : HTMLElement option =
        let found = document.querySelectorAll "[data-term-resize]"
        [ for i in 0 .. found.length - 1 -> found.[i] :?> HTMLElement ]
        |> List.tryFind (fun handle -> handle.contains (unbox target))

    Split.apply (Split.seed ())
    window.addEventListener ("resize", fun _ -> Split.apply (Split.current ()))

    document.addEventListener (
        "pointerdown",
        fun event ->
            match handleUnder event.target with
            | None -> ()
            | Some handle ->
                let event = event :?> PointerEvent
                event.preventDefault ()
                handle.focus ()
                handle.setPointerCapture event.pointerId
                document.documentElement.classList.add "term-resizing"
                // The pane's edge is on the LEFT of a right-hand column, so its width is the
                // distance from the pointer to the right of the window.
                let move = fun (moving: Event) ->
                    Split.apply (window.innerWidth - (moving :?> PointerEvent).clientX)
                let rec finish =
                    fun (_: Event) ->
                        document.documentElement.classList.remove "term-resizing"
                        handle.removeEventListener ("pointermove", move)
                        handle.removeEventListener ("pointerup", finish)
                        handle.removeEventListener ("pointercancel", finish)
                handle.addEventListener ("pointermove", move)
                handle.addEventListener ("pointerup", finish)
                handle.addEventListener ("pointercancel", finish))

    document.addEventListener (
        "keydown",
        fun event ->
            match handleUnder event.target with
            | None -> ()
            | Some _ ->
                let event = event :?> KeyboardEvent
                // Left GROWS this column, because the column is on the right and its edge is
                // what moves.
                let step = if event.shiftKey then 64.0 else 16.0
                let moved =
                    match event.key with
                    | "ArrowLeft" -> Some (Split.current () + step)
                    | "ArrowRight" -> Some (Split.current () - step)
                    | "Home" -> Some (Split.widest ())
                    | "End" -> Some Split.minPane
                    | _ -> None
                match moved with
                | None -> ()
                | Some width ->
                    Split.apply width
                    event.preventDefault ())
