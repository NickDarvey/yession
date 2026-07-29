module Yession.Host.RegistryHub

// The Manager side of the session registry stream (`/sessions/stream`, docs/plans/09):
// the set of Running sessions, published to whoever serves them — an operator's
// serving binding (e.g. registry → `tailscale serve`) subscribes here through the
// management endpoint. Like the MCP hub, this is a Manager-level resource with
// retained-snapshot semantics: a new subscriber is IMMEDIATELY handed the current
// frame (so one connect-read-disconnect is a poll), then every transition pushes a
// fresh FULL frame to all subscribers — snapshots, never deltas, so a reconnect is
// the whole recovery protocol.
//
// Pure of any HTTP: the sink is `SessionRegistryFrame -> unit` (the management route
// supplies one that encodes + writes SSE), so this module is unit-testable without a
// socket.

open Yession.Manager

type RegistryHub =
    { /// Register a subscriber sink. It is invoked IMMEDIATELY with the current frame (the
      /// retained snapshot), then again on every subsequent `Publish`. Returns an unsubscribe.
      Register : (ControlWire.SessionRegistryFrame -> unit) -> (unit -> unit)
      /// Replace the current frame and push it to every subscriber — called by the
      /// Manager on every launch, exit, and display-name change.
      Publish : ControlWire.SessionRegistryFrame -> unit
      /// The current retained frame (observability for tests/ops).
      Current : unit -> ControlWire.SessionRegistryFrame }

/// Create an empty hub — correct at boot, where a Manager restart has stopped every
/// session. Single-threaded (the Node event loop), so the mutable state needs no
/// synchronisation; ids make unsubscribe exact.
let create () : RegistryHub =
    let mutable current : ControlWire.SessionRegistryFrame = { Sessions = [] }
    let mutable sinks : Map<int, ControlWire.SessionRegistryFrame -> unit> = Map.empty
    let mutable nextId = 0

    let register (sink: ControlWire.SessionRegistryFrame -> unit) : (unit -> unit) =
        let id = nextId
        nextId <- nextId + 1
        sinks <- Map.add id sink sinks
        // The retained snapshot: hand the subscriber the current frame at once.
        sink current
        fun () -> sinks <- Map.remove id sinks

    let publish (frame: ControlWire.SessionRegistryFrame) : unit =
        current <- frame
        sinks |> Map.iter (fun _ sink -> sink frame)

    { Register = register
      Publish = publish
      Current = fun () -> current }
