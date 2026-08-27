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

[<Emit("(function (fs, path) { try { fs.accessSync(path, fs.constants.X_OK); return true } catch { return false } })($0, $1)")>]
let private accessXSyncImpl (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSyncImpl (fs: obj) (path: string) : string = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirSyncImpl (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.renameSync($1, $2)")>]
let private renameSyncImpl (fs: obj) (from: string) (dest: string) : unit = jsNative

// Write + fsync the temp file before the rename, so the rename can never expose
// un-flushed content.
[<Emit("(function (fs, path, text) { const fd = fs.openSync(path, 'w'); fs.writeSync(fd, text); fs.fsyncSync(fd); fs.closeSync(fd) })($0, $1, $2)")>]
let private writeSyncedImpl (fs: obj) (path: string) (text: string) : unit = jsNative

let exists (path: string) : bool = existsSyncImpl fs path

/// Can THIS process execute that path? Not the same question as `exists` — a path can be
/// there and unrunnable — and it is the question a named tool has to answer before its
/// absence is blamed for anything.
let executable (path: string) : bool = accessXSyncImpl fs path

/// Create a directory (and any missing parents); a no-op when it already exists.
let ensureDir (path: string) : unit = mkdirSyncImpl fs path

let readText (path: string) : string = readFileSyncImpl fs path

/// Move a path. Within one filesystem this is atomic, which is what lets a thing be built
/// out of sight and then APPEAR whole — the guarantee `writeTextAtomic` below leans on for
/// a file, and the repo manager's clone leans on for a directory.
let rename (from: string) (dest: string) : unit = renameSyncImpl fs from dest

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

[<ImportAll("node:path")>]
let private nodePath : obj = jsNative

[<Emit("$0.resolve($1)")>]
let private resolveImpl (path: obj) (target: string) : string = jsNative

/// A path as an ABSOLUTE one, resolved against the process's working directory.
///
/// The rule it exists for: a path that is stored, handed to another process, or used as
/// BOTH a working directory and an argument must not depend on where anybody happens to
/// stand. `git -C <p>` run with the cwd already set to `p`'s parent resolves `p` twice —
/// so a relative repos directory made every verb say `cannot change to ...: No such file
/// or directory` about a checkout that was sitting right there.
let absolute (path: string) : string = resolveImpl nodePath path

[<Emit("$0.realpathSync($1)")>]
let private realpathSyncImpl (fs: obj) (path: string) : string = jsNative

/// The path the KERNEL will check, with every symlink on the way in resolved.
///
/// Two paths that name one file are not interchangeable to a sandbox: srt canonicalises an
/// allow-list entry, and the OS then denies reading the symlink NODES an access traverses —
/// macOS's escape hatch is `file-read-metadata` on DIRECTORIES, and `/etc`, `/tmp` and
/// `/run` are all symlinks there. So a grant written one way is a denial used the other, and
/// the difference is invisible until something fails far downstream.
///
/// `None` when the path does not resolve — a directory a tool has yet to create is ordinary,
/// and refusing it here would be a different rule wearing this one's name.
let canonical (path: string) : string option =
    try Some (realpathSyncImpl fs path) with _ -> None
