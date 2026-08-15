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
      /// The bounds of what a caller sitting at line `after` has not seen (Plan 22), capped
      /// at `TranscriptChunk.size` lines — `None` when it is current, or when the terminal
      /// has no transcript at all. The two are the same answer on purpose: a cursor says
      /// where the lines are, and there being none is not an error.
      BoundsAfter : TerminalId -> int option -> (int * int) option
      /// The raw lines `[first, last]` of a terminal's transcript — the bytes the range
      /// address promised, or `None` when the transcript does not reach `last` yet.
      /// Answering short would put a partial answer at an address that named the whole
      /// range, and a client keeps that for ever.
      ///
      /// Raw, not decoded: this serves an HTTP range, and what a client stores has to be
      /// what the file says. `ReadRange` below is the decoded read, and they are separate
      /// because a context pack and a cacheable slice want opposite things from a line
      /// that will not parse.
      ReadLines : TerminalId -> int -> int -> string list option
      /// Decoded records over a half-open line range, for a caller that wants what a
      /// block PRINTED rather than a cacheable slice of file (Plan 13, stage 3a).
      /// A line that will not decode is skipped rather than failing the read: this
      /// serves a context pack, and one unreadable record must not cost the agent
      /// every other block's output.
      ReadRange : ReadTranscript
      /// Record the screen as it stands immediately before the next transcript line
      /// (Plan 14, stage 3). Written at every range START — each block's `FromSeq`, each
      /// lease stretch's — because those are the only positions a ranged replay ever asks
      /// for. Writing one at every range END too would be keyframes nothing reads.
      AppendKeyframe : TerminalId -> TranscriptKeyframe -> unit
      /// The keyframe for exactly this line, if one was written. `None` for a recording
      /// made before keyframes existed, which is a degradation the ranged replay states
      /// rather than a failure.
      ReadKeyframe : TerminalId -> int -> TranscriptKeyframe option }

/// The bounds of what a caller at `after` has not seen, over a transcript of `total` lines.
/// One place, so the in-memory store and the file-backed one cannot disagree about what a
/// cursor means — and so the contract the client numbers by (an answer to `after n` begins
/// at `n + 1`) is stated once, here, rather than implied twice.
let private boundsIn (total: int) (after: int option) : (int * int) option =
    let first = match after with Some a -> a + 1 | None -> 0
    if first >= total then None
    else Some (first, min (total - 1) (first + TranscriptChunk.size - 1))

/// The raw lines `[first, last]`, or `None` when the transcript does not reach `last`.
let private linesIn (first: int) (last: int) (lines: string list) : string list option =
    if first < 0 || last < first || last >= List.length lines then None
    else lines |> List.skip first |> List.truncate (last - first + 1) |> Some

/// Decode the records in `[fromSeq, toSeq)` of a transcript's lines. `None` as the end
/// means "whatever it has now", which is what a still-running block has. The header sits
/// at line 0 and simply does not decode as a record, so it needs no special case.
let private recordsIn (fromSeq: int) (toSeq: int option) (lines: string list) : TranscriptRecord list =
    let total = List.length lines
    let last = min total (defaultArg toSeq total)
    let first = max 0 fromSeq
    if last <= first then []
    else
        lines
        |> List.skip first
        |> List.truncate (last - first)
        |> List.choose (fun line ->
            match Codec.fromString Codec.transcriptLine line with
            | Ok (TranscriptRecordLine record) -> Some record
            | _ -> None)

/// A transcript held only in memory: nothing is written, everything is readable for the
/// life of the process. The default when a session has no data directory — a test host,
/// or a session started without persistence — so terminals work identically there and the
/// only thing missing is the part that outlives the process.
let inMemory () : TranscriptStore =
    let files = Collections.Generic.Dictionary<string, ResizeArray<string>> ()
    let keyframes = Collections.Generic.Dictionary<string, ResizeArray<TranscriptKeyframe>> ()

    let linesFor (id: TerminalId) =
        let key = TerminalId.value id
        match files.TryGetValue key with
        | true, lines -> lines
        | _ ->
            let lines = ResizeArray<string> ()
            files.[key] <- lines
            lines

    let appendKeyframe (id: TerminalId) (keyframe: TranscriptKeyframe) =
        let key = TerminalId.value id
        let existing = match keyframes.TryGetValue key with | true, k -> k | _ -> ResizeArray<TranscriptKeyframe> ()
        existing.Add keyframe
        keyframes.[key] <- existing

    { Open =
        fun id header ->
            let lines = linesFor id
            if lines.Count = 0 then lines.Add (Codec.toString Codec.transcriptLine (TranscriptHeaderLine header))
            { Append =
                fun record ->
                    let seq = lines.Count
                    lines.Add (Codec.toString Codec.transcriptLine (TranscriptRecordLine record))
                    seq
              NextSeq = fun () -> lines.Count
              Keyframe = appendKeyframe id }
      BoundsAfter =
        fun id after ->
            match files.TryGetValue (TerminalId.value id) with
            | false, _ -> None
            | true, lines -> boundsIn lines.Count after
      ReadLines =
        fun id first last ->
            match files.TryGetValue (TerminalId.value id) with
            | false, _ -> None
            | true, lines -> lines |> List.ofSeq |> linesIn first last
      ReadRange =
        fun id fromSeq toSeq ->
            match files.TryGetValue (TerminalId.value id) with
            | false, _ -> []
            | true, lines -> lines |> List.ofSeq |> recordsIn fromSeq toSeq
      AppendKeyframe = appendKeyframe
      ReadKeyframe =
        fun id seq ->
            match keyframes.TryGetValue (TerminalId.value id) with
            | false, _ -> None
            | true, all -> all |> Seq.tryFind (fun k -> k.Seq = seq) }

/// A transcript store backed by `<directory>/<terminal>.cast` files.
///
/// `directory` is created on demand. The store keeps each open transcript's line count in
/// memory (that is what a sequence number is), and re-reads the file for a chunk request —
/// chunk reads are rare (a client catching up), appends are not.
let openStore (directory: string) : TranscriptStore =
    mkdirSync directory

    let pathOf (id: TerminalId) = sprintf "%s/%s.cast" directory (TerminalId.value id)
    let keyPathOf (id: TerminalId) = sprintf "%s/%s.keys.jsonl" directory (TerminalId.value id)

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
    let keyHandles = Collections.Generic.Dictionary<string, int> ()

    /// Its own file, never the `.cast`: Plan 13 bought a standard, replayable format on
    /// purpose, and a private record type inside it spends that. Same write-then-fsync
    /// discipline as the transcript — a keyframe a crash lost would silently downgrade a
    /// ranged replay to the naive slice.
    let appendKeyframe (id: TerminalId) (keyframe: TranscriptKeyframe) =
        let fd =
            match keyHandles.TryGetValue (TerminalId.value id) with
            | true, existing -> existing
            | _ ->
                let fd = openAppend (keyPathOf id)
                keyHandles.[TerminalId.value id] <- fd
                fd
        writeAndSync fd (Codec.toString Codec.transcriptKeyframe keyframe + "\n")

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
              NextSeq = fun () -> count.Value
              Keyframe = appendKeyframe id }

    { Open = openTranscript
      BoundsAfter =
        fun id after ->
            let path = pathOf id
            if not (existsSync path) then None
            else boundsIn (List.length (fst (readLines path))) after
      ReadLines =
        fun id first last ->
            let path = pathOf id
            if not (existsSync path) then None
            else readLines path |> fst |> linesIn first last
      ReadRange =
        fun id fromSeq toSeq ->
            let path = pathOf id
            if not (existsSync path) then []
            else
                let lines, _ = readLines path
                lines |> recordsIn fromSeq toSeq
      AppendKeyframe = appendKeyframe
      ReadKeyframe =
        fun id seq ->
            let path = keyPathOf id
            if not (existsSync path) then None
            else
                readLines path
                |> fst
                |> List.tryPick (fun line ->
                    match Codec.fromString Codec.transcriptKeyframe line with
                    | Ok k when k.Seq = seq -> Some k
                    | _ -> None) }

/// The store as the session's HTTP read surface: a cursor, the ranges it resolves to, and the
/// keyframes beside them (docs/plans/22).
///
/// Here rather than at the composition root, because every rule in it is a rule about THIS
/// state — what a terminal id has to be, what a cursor means, what a range that runs off the
/// end answers. A composition root that computed those would put them where only a whole
/// running session could test them; here, a store and a fake token check are enough.
let endpoint (validateToken: string -> bool) (store: TranscriptStore) : Signalling.TranscriptEndpoint =
    // An unparseable id is a terminal that does not exist, which is exactly what an unknown
    // one is — same answer, one code path.
    let forTerminal (terminal: string) (read: TerminalId -> 'a option) : Async<'a option> =
        async {
            match TerminalId.create terminal with
            | Ok id -> return read id
            | Error _ -> return None
        }

    { ValidateToken = validateToken
      BoundsAfter = fun terminal after -> forTerminal terminal (fun id -> store.BoundsAfter id after)
      ReadRange = fun terminal first last -> forTerminal terminal (fun id -> store.ReadLines id first last)
      ReadKeyframe =
        fun terminal seq ->
            forTerminal terminal (fun id ->
                store.ReadKeyframe id seq |> Option.map (Codec.toString Codec.transcriptKeyframe)) }
