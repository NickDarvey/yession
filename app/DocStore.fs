module Yession.Host.DocStore

// Durable Yjs-document persistence (Phase 3, Step 19): a sidecar JSONL file next to the
// event log holding every doc update as one base64 line, so unsent collaborative state
// (queued messages, drafts) survives a process restart. Same discipline as the event
// log: write+fsync before returning (durability before visibility), torn-tail recovery
// for the final unacknowledged line, loud failure on real corruption. At open the file
// is replayed into the session doc (Yjs updates are idempotent and order-tolerant) and
// then COMPACTED to a single merged-state snapshot line.

open Fable.Core
open Node.Api
open Yession.Domain

// The plain reads/writes go through the maintained Fable.Node `fs` binding.
let private existsSync (path: string) : bool = fs.existsSync (U2.Case1 path)
let private readFileSync (path: string) : string = fs.readFileSync (path, "utf8")
let private writeFileSync (path: string) (text: string) : unit = fs.writeFileSync (path, box text)

// Kept as custom interop over the same `fs` module: Fable.Node's binding has no string
// `writeSync` overload, no append-flag `openSync` helper, and no recursive `mkdirSync`.
[<Emit("$0.openSync($1, 'a')")>]
let private openSyncAppend (fs: obj) (path: string) : int = jsNative

[<Emit("(function (fs, fd, text) { return (fs.writeSync(fd, text), fs.fsyncSync(fd)) })($0, $1, $2)")>]
let private writeSyncFsync (fs: obj) (fd: int) (text: string) : unit = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirRecursive (fs: obj) (path: string) : unit = jsNative

let private openAppend (path: string) : int = openSyncAppend (box fs) path
let private writeAndSync (fd: int) (text: string) : unit = writeSyncFsync (box fs) fd text
let private mkdirSync (path: string) : unit = mkdirRecursive (box fs) path
let private splitLines (s: string) : string array = s.Split '\n'

type DocStore =
    { /// Replay every persisted update into the doc, then compact the file to one
      /// snapshot line of the doc's merged state. Call exactly once, at boot, before
      /// wiring the persistence tap (so replayed updates are not re-appended).
      ReplayInto : Yjs.Y.Doc -> unit
      /// Persist one update payload (base64), durably, before returning.
      Append : string -> unit }

/// Open (or create) the doc-update store at `path`.
let openStore (path: string) : DocStore =
    let directory =
        let idx = path.LastIndexOf '/'
        if idx > 0 then path.Substring (0, idx) else ""
    if directory <> "" then mkdirSync directory

    let lines, tornTail =
        if existsSync path then
            let content = readFileSync path
            let lines = splitLines content |> Array.filter (fun l -> l.Trim().Length > 0)
            // A crash can tear the FINAL append (no trailing newline yet): that write
            // was never acknowledged, so it is safe to drop if it fails to apply.
            lines, (not (content.EndsWith "\n") && lines.Length > 0)
        else
            [||], false

    let mutable fd : int option = None

    let replayInto (doc: Yjs.Y.Doc) : unit =
        match fd with
        | Some _ -> failwithf "doc store %s: ReplayInto must run exactly once, at boot" path
        | None ->
            lines
            |> Array.iteri (fun i line ->
                try
                    DocSync.applyRemote doc line
                with e ->
                    if tornTail && i = lines.Length - 1 then
                        eprintfn "doc store %s: dropping torn unacknowledged tail line" path
                    else
                        failwithf "corrupt doc store %s: %s" path e.Message)
            // Compaction: the whole history collapses into one merged snapshot line
            // (Yjs updates merge losslessly), so the file never grows unbounded and
            // the next open replays a single update.
            writeFileSync path (DocSync.fullState doc + "\n")
            fd <- Some (openAppend path)

    let append (payload: string) : unit =
        match fd with
        | Some fd ->
            // Durability before visibility: one write() of the whole line (atomic
            // under O_APPEND) followed by fsync.
            writeAndSync fd (payload + "\n")
        | None -> failwithf "doc store %s: Append before ReplayInto" path

    { ReplayInto = replayInto
      Append = append }
