namespace Fable.BrowserExtras

// Bindings for the browser's `ResizeObserver`: a callback when an element's BOX changes,
// however it changed — a stylesheet, a parent's layout, a custom property, the window. That
// last clause is the whole reason to want one, because it is exactly the set of changes no
// render loop can see: nothing was dispatched, so nothing re-measured.
//
// Declared here rather than reached for with an `[<Emit>]` at the call site for the reason
// every binding in this repository is: an emit body is inlined into whatever calls it, so it
// is invisible to a reader of that file, unreachable from any other, and — the sharp edge —
// Fable does not treat a change to one as a change to its callers. Editing an emit leaves
// every compiled caller stale, which reads as a test failing for a reason the source does not
// contain. A binding project has none of those problems: it is a module, so it recompiles what
// depends on it.
//
// Only the slice actually used is declared. `ResizeObserverEntry` carries `contentRect`,
// `borderBoxSize` and friends; nothing here reads them, because the callback's value is that
// it FIRED — what the new size is gets measured from the element the same way it is measured
// on any other pass, so a second source for it could only disagree.

open Fable.Core
open Browser.Types

[<AllowNullLiteral>]
type ResizeObserver =
    /// Start reporting changes to this element's box. Fires once on `observe` with the
    /// element's current size, which is a feature rather than a quirk: a caller that wants
    /// the size measured is spared arranging the first pass itself.
    abstract observe : element: Element -> unit
    /// Stop reporting changes to one element, leaving the rest observed.
    abstract unobserve : element: Element -> unit
    /// Stop reporting everything. An observer holds a strong reference to what it observes,
    /// so an observer left behind for an element that has gone is a leak — with the callback
    /// still live to fire into whatever closed over it.
    abstract disconnect : unit -> unit

[<AutoOpen>]
module ResizeObserver =

    /// `new ResizeObserver(callback)`. The callback takes the entries and the observer; this
    /// declares neither, because the answer to "which element" is "the one you observed" as
    /// long as one observer watches one element — which is how every caller here uses it, and
    /// a rule the type cannot state but the call site can keep.
    [<Emit("new ResizeObserver($0)")>]
    let create (onResized: unit -> unit) : ResizeObserver = jsNative

    /// Whether this browser has one at all. Universally supported for years, so this is not a
    /// fallback path so much as an honest answer for a headless or synthetic host: a caller
    /// that would otherwise construct `undefined` and fail on the first `observe`.
    [<Emit("typeof ResizeObserver !== 'undefined'")>]
    let isSupported () : bool = jsNative

/// The slice of the CSSOM the shell writes its layout through, which `Fable.Browser.Dom` does
/// not type: it stops at the DOM, and `element.style` belongs to the CSS bindings this
/// repository does not otherwise need.
///
/// Lengths the stylesheet owns, in both directions. The shell keeps its one layout number —
/// the width of the terminals column — as `--term-w` on the root element rather than in the
/// model, because it is presentation: a Lit re-render must not fight it, and a number of pixels
/// is not a fact about the session. The other direction is the same rule read backwards: what a
/// box is padded by is the stylesheet's to say, and code that measures a box must ask rather
/// than carry its own copy of the number.
[<AutoOpen>]
module Css =

    /// Set a custom property on an element. `value` carries its own unit — `"420px"`, not `420`.
    [<Emit("$0.style.setProperty($1, $2)")>]
    let setStyleProperty (element: Browser.Types.HTMLElement) (name: string) (value: string) : unit =
        jsNative

    /// Read a custom property back off an element's own inline style — what was SET, not what
    /// was computed. Empty when it is not set here, which is the honest answer: a property this
    /// element does not carry is not a number to fall back on.
    [<Emit("$0.style.getPropertyValue($1)")>]
    let styleProperty (element: Browser.Types.HTMLElement) (name: string) : string = jsNative

    /// Read a property as the cascade RESOLVED it — the design token, whichever stylesheet
    /// defined it. The counterpart to the one above: that answers "did anybody set this here",
    /// this answers "what is it".
    ///
    /// `getPropertyValue` is the CSSOM's general accessor, so this reads an ordinary property
    /// (`"padding-left"`, answered in pixels) as readily as a custom one. Both callers here
    /// want the same thing from it — a length the stylesheet decided, that no F# may assume.
    [<Emit("getComputedStyle($0).getPropertyValue($1)")>]
    let computedProperty (element: Browser.Types.HTMLElement) (name: string) : string = jsNative

/// Scrolling something into view, where the ALIGNMENT matters.
///
/// `Browser.Dom`'s `scrollIntoView ()` takes no options and therefore always aligns to the
/// start of the scrollport. That is the wrong end whenever the scrollport pins anything at
/// its top: the thing scrolled to arrives underneath it.
[<AutoOpen>]
module Scrolling =

    /// Scroll an element to the MIDDLE of its scrollport.
    ///
    /// Two reasons over the top: what is pinned at the top of a scrollport covers whatever
    /// aligns there — the timeline pins the author line, so a jump landed its target under
    /// the very line saying who was speaking — and a moment somebody has come back to is
    /// worth showing with the moments around it, which is most of why they came back.
    [<Emit("$0.scrollIntoView({ block: 'center' })")>]
    let scrollIntoMiddle (element: Browser.Types.HTMLElement) : unit = jsNative
