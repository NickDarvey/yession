namespace Fable.ResizeObserver

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
