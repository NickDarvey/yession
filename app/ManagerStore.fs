module Yession.Host.ManagerStore

// Durable Manager state (Phase 4, Step 22): one JSON file behind the explicit
// `ManagerCodec` — the codec is the only path between `ManagerState` and bytes, so the
// eventual SQLite move swaps this adapter and nothing else. Writes are atomic via the
// shared `Fs` primitives (temp file + fsync + rename): a crash mid-write leaves the
// previous state intact, never a half-written file. A missing file is the empty state;
// a corrupt file fails loudly — state must never be silently reset.

open Yession.Manager

/// Load the Manager state; a missing file is `ManagerState.empty`.
let load (path: string) : ManagerState =
    if Fs.exists path then
        match ManagerCodec.fromString (Fs.readText path) with
        | Ok state -> state
        | Error e -> failwithf "corrupt manager state %s: %s" path e
    else
        ManagerState.empty

/// Persist the Manager state atomically; durable before returning.
let save (path: string) (state: ManagerState) : unit =
    Fs.writeTextAtomic path (ManagerCodec.toString state)
