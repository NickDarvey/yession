module Yession.Host.KeyedRetainedHub

// A per-session retained broadcast (Plan 17): the Manager holds a current value FOR EACH
// SESSION and hands it to every live subscriber of that session, immediately on subscribe
// and again on every change.
//
// Neither existing hub is this, and the difference is the whole reason it exists:
//
//                        | keyed per session | retained snapshot
//   NotificationHub<'n>  | yes, by secret    | no — "nothing to retain, only future pushes"
//   RetainedHub<'a>      | no, Manager-wide  | yes
//
// It splits the two keys rather than picking one, because they answer different questions:
//
//   * RETENTION is by SESSION, because the value must survive a relaunch. A restarted
//     session subscribes under a NEW secret and must be handed the current value, not an
//     empty one.
//   * DELIVERY is by SECRET, reusing `NotificationHub`'s rule verbatim: a sink dies exactly
//     when its launch does, at the same moment the Manager revokes that secret's
//     capabilities.
//
// So a subscriber names both — which session's value it wants, and which launch is asking.
// The unkeyed `RetainedHub` stays for the session registry, which is genuinely global; two
// hubs for two genuinely different shapes is not redundancy.
//
// Pure of any HTTP: a sink is `Sink<'a>`, so this is unit-testable without a socket.
// Single-threaded (the Node event loop), so the mutable maps need no synchronisation.

open Yession.Domain

type KeyedRetainedHub<'a> =
    { /// Subscribe to one session's value under a launch secret. The sink is invoked
      /// IMMEDIATELY with that session's current value, then again on every `Publish` for
      /// it. Returns an unsubscribe that removes exactly this sink.
      Register : SessionId -> string -> Subscribe<'a>
      /// Replace one session's value and push it to that session's live subscribers.
      /// Retained even when nobody is listening — a session not yet launched must find its
      /// value waiting when it subscribes.
      Publish : SessionId -> Sink<'a>
      /// Drop every sink under a secret (its launch ended). The RETAINED values are
      /// untouched: the declaration outlives the launch, which is the point of keying them
      /// differently.
      Drop : string -> unit
      /// One session's current value (observability for tests/ops).
      Current : SessionId -> 'a }

/// Create a hub whose sessions all start at `initial` until something publishes for them.
let create (initial: 'a) : KeyedRetainedHub<'a> =
    // session -> value. Absent means nobody has published for that session yet, which is
    // `initial` — stored sparsely so a Manager with a thousand sessions and one declaration
    // holds one entry.
    let mutable retained : Map<string, 'a> = Map.empty
    // session -> (sink id -> secret * sink). Nested so publish-by-session is one lookup;
    // the secret rides the entry so drop-by-secret needs no second index.
    let mutable sinks : Map<string, Map<int, string * Sink<'a>>> = Map.empty
    let mutable nextId = 0

    let currentOf (key: string) = Map.tryFind key retained |> Option.defaultValue initial

    let register (sessionId: SessionId) (secret: string) (sink: Sink<'a>) : Subscription =
        let key = SessionId.value sessionId
        let id = nextId
        nextId <- nextId + 1
        let forSession = Map.tryFind key sinks |> Option.defaultValue Map.empty
        sinks <- Map.add key (Map.add id (secret, sink) forSession) sinks
        // The retained snapshot: hand the subscriber this session's current value at once.
        sink (currentOf key)
        Subscription.ofStop (fun () ->
            match Map.tryFind key sinks with
            | Some forSession ->
                let remaining = Map.remove id forSession
                sinks <-
                    if Map.isEmpty remaining then Map.remove key sinks
                    else Map.add key remaining sinks
            | None -> ())

    let publish (sessionId: SessionId) (value: 'a) : unit =
        let key = SessionId.value sessionId
        retained <- Map.add key value retained
        match Map.tryFind key sinks with
        | Some forSession -> forSession |> Map.iter (fun _ (_, sink) -> sink value)
        | None -> ()

    let drop (secret: string) : unit =
        sinks <-
            sinks
            |> Map.map (fun _ forSession -> forSession |> Map.filter (fun _ (owner, _) -> owner <> secret))
            |> Map.filter (fun _ forSession -> not (Map.isEmpty forSession))

    { Register = register
      Publish = publish
      Drop = drop
      Current = fun sessionId -> currentOf (SessionId.value sessionId) }
