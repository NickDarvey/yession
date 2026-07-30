namespace Yession.Domain

// The push vocabulary, named once for the whole repo. Every push seam here — the Manager's hubs,
// the SSE routes and their clients, the client's draft-slot observer — is the same two ideas: a
// sink that receives payloads, and a registration that hands back its teardown. Naming them makes
// those signatures read as prose instead of a chain of parenthesised arrows, and makes the shapes
// obviously interchangeable (a hub's `Register` IS a `Subscribe`).

/// A live subscription's teardown. Idempotent: safe to stop twice, or after whatever was
/// subscribed to is gone.
///
/// A RECORD rather than a bare `unit -> unit`, and that is load-bearing under Fable. A function
/// returning a function is one curried function of arity 2, so a `Subscribe` passed as a parameter
/// compiles to `curry2(subscribe)(sink)` — which returns a closure INSTEAD of subscribing, and only
/// registers the sink if the caller ever "unsubscribes". Nothing in the type system catches that;
/// the stream simply never delivers. A value cannot be curried, so the shape below cannot regress
/// that way. (Same reason `Editor.EditorHandle` carries its `Dispose`.)
type Subscription = { Stop : unit -> unit }

/// Where a stream's payloads go — an HTTP response writer, a decoder, a test collector.
type Sink<'a> = 'a -> unit

/// Registers a sink and hands back its teardown. A retained-snapshot hub invokes the sink once
/// with the current value before returning; a plain fan-out does not.
type Subscribe<'a> = Sink<'a> -> Subscription

module Subscription =

    /// The teardown for a subscription that needs none.
    let none : Subscription = { Stop = ignore }

    /// Wrap a teardown function as a `Subscription`.
    let ofStop (stop: unit -> unit) : Subscription = { Stop = stop }
