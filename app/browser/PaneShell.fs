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

/// Hand focus to whichever replay control is on screen for this terminal (Plan 14, stage 7).
/// The way in and the way out swap each other out of the document — rewind for Jump-to-live
/// on a live terminal, play for Back-to-blocks on a closed one — so the press that swaps
/// them, or the catch-up that fires when a rewound cast plays off its end, would otherwise
/// strand focus on a control that has gone. One selector for all four: a terminal renders
/// exactly one of them at a time. Guarded on focus actually being stranded (on `body`, or
/// inside the player that is being unmounted): the automatic catch-up must never yank focus
/// out of a composer somebody is typing in.
[<Emit("""(function (terminalId) { return (
requestAnimationFrame(() => {
  const active = document.activeElement
  const stranded = !active || active === document.body || active.closest('[data-pane-replay]')
  if (!stranded) return
  const next = document.querySelector(
    '[data-terminal-live="' + terminalId + '"], [data-terminal-rewind="' + terminalId + '"], ' +
    '[data-terminal-blocks="' + terminalId + '"], [data-terminal-play="' + terminalId + '"]')
  if (next) next.focus()
})
) })($0)""")>]
let toDvrControl (terminalId: string) : unit = jsNative

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
