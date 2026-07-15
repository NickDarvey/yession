module Yession.Host.ManagerStore

// Durable Manager state (Phase 4, Step 22): one JSON file behind the explicit
// `ManagerCodec` — the codec is the only path between `ManagerState` and bytes, so the
// eventual SQLite move swaps this adapter and nothing else. Writes are atomic
// (temp file + fsync + rename): a crash mid-write leaves the previous state intact,
// never a half-written file. A missing file is the empty state; a corrupt file fails
// loudly — state must never be silently reset.

open Fable.Core
open Yession.Manager

[<ImportAll("node:fs")>]
let private fs : obj = jsNative

[<Emit("$0.existsSync($1)")>]
let private existsSync (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirSync (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.renameSync($1, $2)")>]
let private renameSync (fs: obj) (from: string) (dest: string) : unit = jsNative

// Write + fsync the temp file before the rename, so the rename can never expose
// un-flushed content.
[<Emit("(() => { const fd = $0.openSync($1, 'w'); $0.writeSync(fd, $2); $0.fsyncSync(fd); $0.closeSync(fd) })()")>]
let private writeSynced (fs: obj) (path: string) (text: string) : unit = jsNative

let private directoryOf (path: string) : string =
    let idx = path.LastIndexOf '/'
    if idx > 0 then path.Substring (0, idx) else ""

/// Load the Manager state; a missing file is `ManagerState.empty`.
let load (path: string) : ManagerState =
    if existsSync fs path then
        match ManagerCodec.fromString (readFileSync fs path) with
        | Ok state -> state
        | Error e -> failwithf "corrupt manager state %s: %s" path e
    else
        ManagerState.empty

/// Persist the Manager state atomically; durable before returning.
let save (path: string) (state: ManagerState) : unit =
    let directory = directoryOf path
    if directory <> "" then mkdirSync fs directory
    let temp = path + ".tmp"
    writeSynced fs temp (ManagerCodec.toString state)
    renameSync fs temp path
