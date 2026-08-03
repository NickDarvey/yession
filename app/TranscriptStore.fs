module Yession.Host.TranscriptStore

// Per-terminal transcripts (Plan 13): one append-only asciicast file per terminal, held
// under the session's data directory as `terminals/<id>.cast`.
//
// Same discipline as the event log and the doc sidecar, because it is the same promise:
// write + fsync before returning, so a record is durable before anyone is told about it.
// The difference is what a torn tail means. A torn event line is dropped, because the
// append was never acknowledged and the log must stay decodable. A torn transcript line
// is dropped for the same reason — but the DROP is not silent: the line count is what
// every sequence number is measured in, so a transcript that quietly lost its last line
// would renumber everything after it. Truncating the file back to its last whole line at
// open keeps line index and sequence number the same thing, for ever.
//
// A terminal's file outlives the process that wrote it. Reopening an existing transcript
// appends to it and continues its numbering; the header is written once, when the file is
// created. An audit trail that restarts at zero is not one.

open System
open Fable.Core
open Node.Api
open Yession.Domain
open Yession.SessionProcess

let private existsSync (path: string) : bool = fs.existsSync (U2.Case1 path)
let private readFileSync (path: string) : string = fs.readFileSync (path, "utf8")
let private writeFileSync (path: string) (text: string) : unit = fs.writeFileSync (path, box text)

// The durability-critical writes, kept as explicit interop over the same `fs` module —
// Fable.Node's binding has no string `writeSync`, no append-flag `openSync`, and no
// recursive `mkdirSync`.
[<Emit("$0.openSync($1, 'a')")>]
let private openSyncAppend (fs: obj) (path: string) : int = jsNative

[<Emit("($0.writeSync($1, $2), $0.fsyncSync($1))")>]
let private writeSyncFsync (fs: obj) (fd: int) (text: string) : unit = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirRecursive (fs: obj) (path: string) : unit = jsNative

let private openAppend (path: string) : int = openSyncAppend (box fs) path
let private writeAndSync (fd: int) (text: string) : unit = writeSyncFsync (box fs) fd text
let private mkdirSync (path: string) : unit = mkdirRecursive (box fs) path

/// Everything the Session Process and its HTTP surface need from transcript storage.
type TranscriptStore =
    { /// Open (or reopen) one terminal's transcript.
      Open : OpenTranscript
      /// Lines `[n*size, (n+1)*size)` of a terminal's transcript, plus whether the chunk
      /// is full (and therefore immutable and cacheable for ever). `None` for a terminal
      /// with no transcript — which is a 404, not an empty chunk: those mean different
      /// things to a client catching up.
      ReadChunk : TerminalId -> int -> (string list * bool) option }

/// A transcript held only in memory: nothing is written, everything is readable for the
/// life of the process. The default when a session has no data directory — a test host,
/// or a session started without persistence — so terminals work identically there and the
/// only thing missing is the part that outlives the process.
let inMemory () : TranscriptStore =
    let files = Collections.Generic.Dictionary<string, ResizeArray<string>> ()

    let linesFor (id: TerminalId) =
        let key = TerminalId.value id
        match files.TryGetValue key with
        | true, lines -> lines
        | _ ->
            let lines = ResizeArray<string> ()
            files.[key] <- lines
            lines

    { Open =
        fun id header ->
            let lines = linesFor id
            if lines.Count = 0 then lines.Add (Codec.toString Codec.transcriptLine (TranscriptHeaderLine header))
            { Append =
                fun record ->
                    let seq = lines.Count
                    lines.Add (Codec.toString Codec.transcriptLine (TranscriptRecordLine record))
                    seq
              NextSeq = fun () -> lines.Count }
      ReadChunk =
        fun id index ->
            match files.TryGetValue (TerminalId.value id) with
            | false, _ -> None
            | true, lines ->
                let first = TranscriptChunk.firstSeq index
                let chunk =
                    if first >= lines.Count then []
                    else lines.GetRange (first, min TranscriptChunk.size (lines.Count - first)) |> List.ofSeq
                Some (chunk, List.length chunk = TranscriptChunk.size) }

/// A transcript store backed by `<directory>/<terminal>.cast` files.
///
/// `directory` is created on demand. The store keeps each open transcript's line count in
/// memory (that is what a sequence number is), and re-reads the file for a chunk request —
/// chunk reads are rare (a client catching up), appends are not.
let openStore (directory: string) : TranscriptStore =
    mkdirSync directory

    let pathOf (id: TerminalId) = sprintf "%s/%s.cast" directory (TerminalId.value id)

    /// The whole lines of a transcript file, and whether the file had to be repaired.
    /// A file not ending in a newline has a torn final append — a write that was never
    /// acknowledged — and it is truncated away, because line index IS sequence number and
    /// a half-written line must never be counted as one.
    let readLines (path: string) : string list * bool =
        if not (existsSync path) then [], false
        else
            let content = readFileSync path
            let lines = content.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0) |> List.ofArray
            if content <> "" && not (content.EndsWith "\n") then
                match List.rev lines with
                | _ :: whole -> List.rev whole, true
                | [] -> [], false
            else lines, false

    let handles = Collections.Generic.Dictionary<string, int * int ref> ()

    let openTranscript : OpenTranscript =
        fun id header ->
            let key = TerminalId.value id
            let path = pathOf id
            let fd, count =
                match handles.TryGetValue key with
                | true, existing -> existing
                | _ ->
                    let existing, torn = readLines path
                    if torn then
                        eprintfn "transcript %s: dropping torn unacknowledged tail line" path
                        writeFileSync path (existing |> List.map (fun l -> l + "\n") |> String.concat "")
                    let fd = openAppend path
                    // A fresh transcript gets its header as line 0. An existing one keeps
                    // the header it opened with — rewriting it would renumber every record
                    // that came after.
                    let count =
                        if List.isEmpty existing then
                            writeAndSync fd (Codec.toString Codec.transcriptLine (TranscriptHeaderLine header) + "\n")
                            ref 1
                        else ref (List.length existing)
                    handles.[key] <- (fd, count)
                    (fd, count)
            { Append =
                fun record ->
                    let seq = count.Value
                    // Durability before visibility: one write() of the whole line (atomic
                    // under O_APPEND) followed by fsync, before the record is broadcast.
                    writeAndSync fd (Codec.toString Codec.transcriptLine (TranscriptRecordLine record) + "\n")
                    count.Value <- seq + 1
                    seq
              NextSeq = fun () -> count.Value }

    { Open = openTranscript
      ReadChunk =
        fun id index ->
            let path = pathOf id
            if not (existsSync path) then None
            else
                let lines, _ = readLines path
                let first = TranscriptChunk.firstSeq index
                let chunk = lines |> List.skip (min first (List.length lines)) |> List.truncate TranscriptChunk.size
                Some (chunk, List.length chunk = TranscriptChunk.size) }
