module Yession.Host.McpHub

// The Manager side of the MCP tool stream (`/control/mcp`, the second reverse leg of the
// control RPC). Unlike environment notifications — a per-secret signal — the MCP tool set is
// a Manager-level resource shared by every session: which MCP services are available is
// infrastructure, the same for all. So this hub is a single retained list broadcast to all
// subscribers, not a per-secret fan-out.
//
// Retained-snapshot semantics are the point: a newly-subscribing session is IMMEDIATELY handed
// the current list (so it "yields the initial set"), then every subsequent `Publish` pushes the
// new full list to all subscribers. A subscriber's stream is torn down when its socket closes
// (the control route unsubscribes on request-close), which is exactly when its session's launch
// ends, so no separate revoke path is needed.
//
// Pure of any HTTP: the sink is `McpToolList -> unit` (the control route supplies one that
// encodes + writes an SSE frame), so this module is unit-testable without a socket.

open Yession.Domain

type McpHub =
    { /// Register a subscriber sink. It is invoked IMMEDIATELY with the current list (the
      /// retained snapshot), then again on every subsequent `Publish`. Returns an unsubscribe.
      Register : (McpToolList -> unit) -> (unit -> unit)
      /// Replace the current tool list and push the new snapshot to every subscriber. This is
      /// how the Manager announces MCP services appearing or disappearing.
      Publish : McpToolList -> unit
      /// The current retained list (observability for tests/ops).
      Current : unit -> McpToolList }

/// Create an empty hub (no tools until something publishes). Single-threaded (the Node event
/// loop), so the mutable state needs no synchronisation; ids make unsubscribe exact.
let create () : McpHub =
    let mutable current = McpToolList.empty
    let mutable sinks : Map<int, McpToolList -> unit> = Map.empty
    let mutable nextId = 0

    let register (sink: McpToolList -> unit) : (unit -> unit) =
        let id = nextId
        nextId <- nextId + 1
        sinks <- Map.add id sink sinks
        // The retained snapshot: hand the subscriber the current list at once.
        sink current
        fun () -> sinks <- Map.remove id sinks

    let publish (list: McpToolList) : unit =
        current <- list
        sinks |> Map.iter (fun _ sink -> sink list)

    { Register = register
      Publish = publish
      Current = fun () -> current }
