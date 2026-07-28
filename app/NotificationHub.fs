module Yession.Host.NotificationHub

// The Manager side of Manager→Session push (the reverse leg of the control RPC):
// a registry of live subscriber sinks, keyed by the per-launch control secret. The
// control endpoint registers one sink per SSE subscription (writing frames to that
// response); the Manager pushes a payload to a session and it fans out to every sink
// whose secret is still live. Keying by secret means a sink dies exactly when its
// launch does — the Manager drops the secret's sinks on child exit, the same moment it
// revokes the secret's capabilities.
//
// Generic over the payload: one mechanism serves `/control/notifications`
// (SessionNotification) and `/control/connections` (ConnectionStatusList). Pure of any
// HTTP: the sink is `'n -> unit` (the control route supplies one that encodes + writes
// SSE), so this module is unit-testable without a socket.

/// The subscriber registry. `Register`/`Drop` mutate it; `NotifySecret` fans a payload
/// out to a secret's current sinks (a no-op for an unknown/dropped secret).
type NotificationHub<'n> =
    { /// Register a sink under a secret; returns an unsubscribe that removes exactly this
      /// sink (idempotent — safe to call after the secret was dropped).
      Register : string -> ('n -> unit) -> (unit -> unit)
      /// Push a payload to every sink currently registered under a secret.
      NotifySecret : string -> 'n -> unit
      /// Drop every sink under a secret (its launch ended); further notifies are no-ops.
      Drop : string -> unit }

/// Create an empty hub. Single-threaded (the Node event loop), so the mutable maps need no
/// synchronisation; ids make removal exact even when a secret has several subscribers.
let create<'n> () : NotificationHub<'n> =
    // secret -> (sink id -> sink). Nested so drop-by-secret and unsubscribe-by-id are both O(log n).
    let mutable sinks : Map<string, Map<int, 'n -> unit>> = Map.empty
    let mutable nextId = 0

    let register (secret: string) (sink: 'n -> unit) : (unit -> unit) =
        let id = nextId
        nextId <- nextId + 1
        let forSecret = Map.tryFind secret sinks |> Option.defaultValue Map.empty
        sinks <- Map.add secret (Map.add id sink forSecret) sinks
        fun () ->
            match Map.tryFind secret sinks with
            | Some forSecret ->
                let remaining = Map.remove id forSecret
                sinks <-
                    if Map.isEmpty remaining then Map.remove secret sinks
                    else Map.add secret remaining sinks
            | None -> ()

    let notifySecret (secret: string) (payload: 'n) : unit =
        match Map.tryFind secret sinks with
        | Some forSecret -> forSecret |> Map.iter (fun _ sink -> sink payload)
        | None -> ()

    let drop (secret: string) : unit = sinks <- Map.remove secret sinks

    { Register = register
      NotifySecret = notifySecret
      Drop = drop }
