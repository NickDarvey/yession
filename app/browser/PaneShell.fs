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
// Both wait a frame: the model changes first, Lit renders second, and `focus()` on an element
// that is not in the document yet is a no-op.

open Fable.Core

[<Emit("""requestAnimationFrame(() => {
  const pane = document.querySelector('[data-pane-panel]')
  if (pane) pane.focus()
})""")>]
let toPane () : unit = jsNative

/// Return focus to the chat item that opened a tab, given that tab's key — the only thing the
/// chip and the tab share. Falls back to the strip's first tab when the item has scrolled out
/// of the rendered chat, because focus has to land somewhere real.
[<Emit("""(function (tabKey) { return (
requestAnimationFrame(() => {
  const parts = tabKey.split(':')
  const selector =
    parts[0] === 'block' ? '[data-chat-block="' + parts[2] + '"]'
    : parts[0] === 'stretch' ? '[data-chat-stretch="' + parts.slice(1).join(':') + '"]'
    : null
  const back = selector && document.querySelector(selector)
  const next = back || document.querySelector('[role="tablist"] [role="tab"]')
  if (next) next.focus()
})
) })($0)""")>]
let toChatItem (tabKey: string) : unit = jsNative

/// Hand focus to a terminal's watch toggle when the reader has been stranded (Plan 14,
/// stage 7; Plan 25, stage 3).
///
/// There used to be four controls here — rewind, jump-to-live, play, back-to-blocks — each
/// swapping another out of the document, so every press risked stranding focus and this had
/// to name all four to catch whichever had survived. One toggle that relabels in place needs
/// none of that: a press keeps its own focus.
///
/// What is left is the case no press causes. A rewound cast playing off its end unmounts the
/// player by itself, under whoever was reading it. Hence the guard: focus is moved only when
/// it is actually stranded (on `body`, or inside the player being unmounted), never yanked
/// out of a composer somebody is typing in.
/// No terminal id: the toggle's VALUE is the face it will show, not which terminal it is
/// about, and the pane shows one tab at a time — so there is exactly one of these in the
/// document and naming a terminal could only ever name it wrongly.
[<Emit("""requestAnimationFrame(() => {
  const active = document.activeElement
  const stranded = !active || active === document.body || active.closest('[data-pane-replay]')
  if (!stranded) return
  const next = document.querySelector('[data-terminal-watch]')
  if (next) next.focus()
})""")>]
let toWatchToggle () : unit = jsNative

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
[<Emit("""(function (terminalId, blockId) { return (
requestAnimationFrame(() => {
  const scrollback = document.querySelector('[data-terminal-scrollback][data-terminal-id="' + terminalId + '"]')
  const block = scrollback && scrollback.querySelector('[data-terminal-block="' + blockId + '"]')
  if (!block) return
  block.scrollIntoView({ block: 'start' })
  block.classList.remove('animate-reveal')
  void block.offsetWidth
  block.classList.add('animate-reveal')
})
) })($0, $1)""")>]
let revealBlock (terminalId: string) (blockId: string) : unit = jsNative

/// The pane's open state, as a class on the shell root — the same mechanism the sidebar uses,
/// so a Lit re-render never fights the CSS transition. A `set` rather than a toggle, because
/// the model holds the bit and this only reflects it: the app opens this column itself
/// whenever a chip or a tab is chosen.
[<Emit("document.documentElement.classList.toggle('term-closed', !$0)")>]
let setOpen (isOpen: bool) : unit = jsNative

/// The pane's width on desktop, as a custom property on the shell root — the same mechanism
/// the open state uses, and for the same reasons: it is presentation, a Lit re-render must not
/// fight it, and the model has no business holding a number of pixels.
///
/// The column was a fixed 420px chosen as "the width the content actually has", and measured
/// against what a terminal actually prints it is 20 columns short of 80. Rather than guess a
/// better constant for every screen, the split moves and is remembered.
///
/// Installed once, delegated from the document so it survives every re-render of the handle.
/// The handle is a `separator` with a value, so the arrow keys have to move it — a splitter
/// that only answers a drag is a control a keyboard cannot reach at all. Bounds keep both
/// columns usable: neither the chat nor the pane can be dragged away to nothing, and the
/// ceiling follows the window so a resize down cannot strand the split off screen.
[<Emit("""(() => {
  const KEY = 'yession:term-width'
  const root = document.documentElement
  const MIN = 320, CHAT_MIN = 420
  // The ceiling is what the CHAT can spare, not what the window is: the sidebar takes 280px
  // of the window and can be collapsed, so a bound measured against `innerWidth` let the pane
  // grow to 932px on a 1440 screen and left the conversation 228px — its title truncated to a
  // single letter and its commands gone. Ask the two columns how wide they actually are.
  const max = () => {
    const pane = document.querySelector('[data-terminal-panel]')
    const chat = document.querySelector('[data-conversation]')
    if (!pane || !chat) return Math.max(MIN, window.innerWidth - CHAT_MIN)
    const spare = pane.getBoundingClientRect().width + chat.getBoundingClientRect().width - CHAT_MIN
    return Math.max(MIN, spare)
  }
  const apply = (w) => {
    const next = Math.max(MIN, Math.min(max(), Math.round(w)))
    root.style.setProperty('--term-w', next + 'px')
    for (const handle of document.querySelectorAll('[data-term-resize]')) {
      handle.setAttribute('aria-valuenow', String(next))
      handle.setAttribute('aria-valuemin', String(MIN))
      handle.setAttribute('aria-valuemax', String(max()))
    }
    try { localStorage.setItem(KEY, String(next)) } catch (e) {}
    return next
  }
  const current = () => {
    const said = parseFloat(root.style.getPropertyValue('--term-w'))
    if (said > 0) return said
    const pane = document.querySelector('[data-terminal-panel]')
    return pane ? pane.getBoundingClientRect().width : MIN
  }
  // Seeded at install, ALWAYS — not only when a width was remembered.
  //
  // Unseeded, `current()` had to fall back to measuring the column, and the column animates:
  // asked while it is opening it answers 1px (a shut pane is its own left border) or whatever
  // the easing has reached, and the arrow keys then step from a number that was never the
  // split. It also left the separator reporting the literal the template ships until somebody
  // resized, which is a value assistive technology reads and nothing had checked.
  let remembered = NaN
  try { remembered = Number(localStorage.getItem(KEY)) } catch (e) {}
  const fromToken = parseFloat(getComputedStyle(root).getPropertyValue('--spacing-term'))
  apply(remembered > 0 ? remembered : (fromToken > 0 ? fromToken : MIN))
  window.addEventListener('resize', () => apply(current()))
  document.addEventListener('pointerdown', (e) => {
    const handle = e.target instanceof Element && e.target.closest('[data-term-resize]')
    if (!handle) return
    e.preventDefault()
    handle.focus()
    handle.setPointerCapture(e.pointerId)
    root.classList.add('term-resizing')
    const move = (ev) => apply(window.innerWidth - ev.clientX)
    const done = () => {
      root.classList.remove('term-resizing')
      handle.removeEventListener('pointermove', move)
      handle.removeEventListener('pointerup', done)
      handle.removeEventListener('pointercancel', done)
    }
    handle.addEventListener('pointermove', move)
    handle.addEventListener('pointerup', done)
    handle.addEventListener('pointercancel', done)
  })
  document.addEventListener('keydown', (e) => {
    const handle = e.target instanceof Element && e.target.closest('[data-term-resize]')
    if (!handle) return
    // Left grows this column, because the column is on the right and its edge is what moves.
    const step = e.shiftKey ? 64 : 16
    if (e.key === 'ArrowLeft') apply(current() + step)
    else if (e.key === 'ArrowRight') apply(current() - step)
    else if (e.key === 'Home') apply(max())
    else if (e.key === 'End') apply(MIN)
    else return
    e.preventDefault()
  })
})()""")>]
let installPaneResize () : unit = jsNative
