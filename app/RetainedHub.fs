module Yession.Host.RetainedHub

// A Manager-level broadcast with retained-snapshot semantics: a new subscriber is IMMEDIATELY
// handed the current value, then every `Publish` pushes a fresh FULL value to all subscribers —
// snapshots, never deltas, so a reconnect is the whole recovery protocol and one
// connect-read-disconnect is a poll.
//
// Generic over the payload, exactly as `NotificationHub<'n>` is: one mechanism serves the session
// registry (`/sessions/stream` and the management page's rows). It publishes the DOMAIN value; each
// route projects that to its own wire format, so one publish can serve consumers that want
// different views of the same change.
//
// Pure of any HTTP: a sink is `Sink<'a>` (the routes supply ones that encode + write SSE), so this
// is unit-testable without a socket. Single-threaded (the Node event loop), so the mutable state
// needs no synchronisation; ids make unsubscribe exact.

open Yession.Domain

type RetainedHub<'a> =
    { /// Register a subscriber sink. It is invoked IMMEDIATELY with the current value (the
      /// retained snapshot), then again on every subsequent `Publish`. Returns an unsubscribe.
      Register : Subscribe<'a>
      /// Replace the current value and push it to every subscriber.
      Publish : Sink<'a>
      /// The current retained value (observability for tests/ops).
      Current : unit -> 'a }

/// Create a hub retaining `initial` until something publishes — the boot value, correct where a
/// Manager restart has stopped every session and no tools have been announced.
let create (initial: 'a) : RetainedHub<'a> =
    let mutable current = initial
    let mutable sinks : Map<int, Sink<'a>> = Map.empty
    let mutable nextId = 0

    let register (sink: Sink<'a>) : Subscription =
        let id = nextId
        nextId <- nextId + 1
        sinks <- Map.add id sink sinks
        // The retained snapshot: hand the subscriber the current value at once.
        sink current
        Subscription.ofStop (fun () -> sinks <- Map.remove id sinks)

    let publish (value: 'a) : unit =
        current <- value
        sinks |> Map.iter (fun _ sink -> sink value)

    { Register = register
      Publish = publish
      Current = fun () -> current }
