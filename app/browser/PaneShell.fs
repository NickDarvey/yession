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
[<Emit("""requestAnimationFrame(() => {
  const parts = $0.split(':')
  const selector =
    parts[0] === 'block' ? '[data-chat-block="' + parts[2] + '"]'
    : parts[0] === 'stretch' ? '[data-chat-stretch="' + parts.slice(1).join(':') + '"]'
    : null
  const back = selector && document.querySelector(selector)
  const next = back || document.querySelector('[role="tablist"] [role="tab"]')
  if (next) next.focus()
})""")>]
let toChatItem (tabKey: string) : unit = jsNative

/// The pane's open state, as a class on the shell root — the same mechanism the sidebar uses,
/// so a Lit re-render never fights the CSS transition. A `set` rather than a toggle, because
/// the model holds the bit and this only reflects it: the app opens this column itself
/// whenever a chip or a tab is chosen.
[<Emit("document.documentElement.classList.toggle('term-closed', !$0)")>]
let setOpen (isOpen: bool) : unit = jsNative
