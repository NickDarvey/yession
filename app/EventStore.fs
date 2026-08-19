module Yession.Host.EventStore

// A durable event log: an append-only JSONL file behind the same `EventLog` capability
// as the in-memory implementation, so callers cannot tell the difference. One line per
// envelope, written synchronously on append (local-first, single writer — the Session
// Process). The log lives with the Session Process on Node, so durability is a file,
// not a browser store: browser clients already recover by offset catch-up (Step 07).

open Fable.Core
open Node.Api
open Yession.Domain
open Yession.SessionProcess

// The plain reads/writes go through the maintained Fable.Node `fs` binding.
let private existsSync (path: string) : bool = fs.existsSync (U2.Case1 path)
let private readFileSync (path: string) : string = fs.readFileSync (path, "utf8")
let private writeFileSync (path: string) (text: string) : unit = fs.writeFileSync (path, box text)

// Kept as custom interop over the same `fs` module: Fable.Node's binding has no string
// `writeSync` overload, no append-flag `openSync` helper, and no recursive `mkdirSync` —
// and these are exactly the durability-critical writes, so they stay explicit.
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

/// Open (or create) a file-backed event log at `path`. Existing lines are replayed into
/// memory at open, so reads are as fast as the in-memory log and offsets continue where
/// the previous process stopped. A malformed line fails loudly — a corrupt log must
/// never be silently truncated.
let openLog (path: string) (sessionId: SessionId) (clock: unit -> System.DateTimeOffset) : EventLog<SessionEvent> =
    let directory =
        let idx = path.LastIndexOf '/'
        if idx > 0 then path.Substring (0, idx) else ""
    if directory <> "" then mkdirSync directory

    let events = ResizeArray<EventEnvelope<SessionEvent>> ()
    if existsSync path then
        let content = readFileSync path
        let lines = splitLines content |> Array.filter (fun l -> l.Trim().Length > 0)
        // A crash can tear the FINAL append (no trailing newline yet): that write was
        // never acknowledged, so it is safe to drop. Any other malformed line means
        // real corruption and fails loudly.
        let tornTail = not (content.EndsWith "\n") && lines.Length > 0
        let mutable dropped = false
        lines
        |> Array.iteri (fun i line ->
            match Codec.fromString Codec.sessionEventEnvelope line with
            | Ok envelope -> events.Add envelope
            | Error e ->
                if tornTail && i = lines.Length - 1 then
                    eprintfn "event log %s: dropping torn unacknowledged tail line" path
                    dropped <- true
                else
                    failwithf "corrupt event log %s: %s" path e)
        // Repair the file before appending again, or the next line would concatenate
        // onto the torn bytes.
        if dropped then
            let valid =
                events
                |> Seq.map (fun e -> Codec.toString Codec.sessionEventEnvelope e + "\n")
                |> String.concat ""
            writeFileSync path valid

    let fd = openAppend path

    let append (actor: ActorRef) (event: SessionEvent) : Async<AppendResult> =
        async {
            let offset =
                match EventOffset.create (int64 events.Count) with
                | Ok o -> o
                | Error e -> failwithf "event log offset invariant violated: %s" e
            let envelope =
                { EventId = EventId.fresh ()
                  SessionId = sessionId
                  Offset = offset
                  Actor = actor
                  Timestamp = clock ()
                  Event = event }
            // Durability before visibility: one write() of the whole line (atomic under
            // O_APPEND) followed by fsync, before the envelope becomes readable.
            writeAndSync fd (Codec.toString Codec.sessionEventEnvelope envelope + "\n")
            events.Add envelope
            return { Offset = offset }
        }

    let read (after: EventOffset option) (limit: int) : Async<EventPage<SessionEvent>> =
        async {
            let afterValue = after |> Option.map EventOffset.value
            let selected =
                events
                |> Seq.filter (fun e ->
                    match afterValue with
                    | Some n -> EventOffset.value e.Offset > n
                    | None -> true)
                |> Seq.toArray
            let pageEvents = selected |> Array.truncate (max 0 limit)
            let lastOffset =
                if pageEvents.Length = 0 then None
                else Some (Array.last pageEvents).Offset
            return
                { Events = List.ofArray pageEvents
                  LastOffset = lastOffset
                  IsEnd = pageEvents.Length = selected.Length }
        }

    { Append = append
      Read = read }
