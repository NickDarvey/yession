module Yession.Host.Fs

// Shared synchronous file primitives for the Manager's durable stores (extracted from
// ManagerStore for Plan 06 so the secrets store gets the identical guarantee): writes
// are atomic — temp file + fsync + rename — so a crash mid-write leaves the previous
// content intact, never a half-written file.

open Fable.Core

[<ImportAll("node:fs")>]
let private fs : obj = jsNative

[<Emit("$0.existsSync($1)")>]
let private existsSyncImpl (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSyncImpl (fs: obj) (path: string) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirSyncImpl (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.renameSync($1, $2)")>]
let private renameSyncImpl (fs: obj) (from: string) (dest: string) : unit = jsNative

// Write + fsync the temp file before the rename, so the rename can never expose
// un-flushed content.
[<Emit("(() => { const fd = $0.openSync($1, 'w'); $0.writeSync(fd, $2); $0.fsyncSync(fd); $0.closeSync(fd) })()")>]
let private writeSyncedImpl (fs: obj) (path: string) (text: string) : unit = jsNative

let exists (path: string) : bool = existsSyncImpl fs path

/// Create a directory (and any missing parents); a no-op when it already exists.
let ensureDir (path: string) : unit = mkdirSyncImpl fs path

let readText (path: string) : string = readFileSyncImpl fs path

let private directoryOf (path: string) : string =
    let idx = path.LastIndexOf '/'
    if idx > 0 then path.Substring (0, idx) else ""

/// Write atomically; durable before returning.
let writeTextAtomic (path: string) (text: string) : unit =
    let directory = directoryOf path
    if directory <> "" then mkdirSyncImpl fs directory
    let temp = path + ".tmp"
    writeSyncedImpl fs temp text
    renameSyncImpl fs temp path
