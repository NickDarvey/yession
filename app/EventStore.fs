module Yession.Host.EventStore

// A durable event log: an append-only JSONL file behind the same `EventLog` capability
// as the in-memory implementation, so callers cannot tell the difference. One line per
// envelope, written synchronously on append (local-first, single writer — the Session
// Process). The log lives with the Session Process on Node, so durability is a file,
// not a browser store: browser clients already recover by offset catch-up (Step 07).

open Fable.Core
open Yession.Domain
open Yession.SessionProcess

[<ImportAll("node:fs")>]
let private fs : obj = jsNative

[<Emit("$0.existsSync($1)")>]
let private existsSync (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = jsNative

[<Emit("$0.openSync($1, 'a')")>]
let private openAppend (fs: obj) (path: string) : int = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFileSync (fs: obj) (path: string) (text: string) : unit = jsNative

[<Emit("($0.writeSync($1, $2), $0.fsyncSync($1))")>]
let private writeAndSync (fs: obj) (fd: int) (text: string) : unit = jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirSync (fs: obj) (path: string) : unit = jsNative

[<Emit("$0.split('\\n')")>]
let private splitLines (s: string) : string array = jsNative

/// Open (or create) a file-backed event log at `path`. Existing lines are replayed into
/// memory at open, so reads are as fast as the in-memory log and offsets continue where
/// the previous process stopped. A malformed line fails loudly — a corrupt log must
/// never be silently truncated.
let openLog (path: string) (sessionId: SessionId) (clock: unit -> System.DateTimeOffset) : EventLog<SessionEvent> =
    let directory =
        let idx = path.LastIndexOf '/'
        if idx > 0 then path.Substring (0, idx) else ""
    if directory <> "" then mkdirSync fs directory

    let events = ResizeArray<EventEnvelope<SessionEvent>> ()
    if existsSync fs path then
        let content = readFileSync fs path
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
            writeFileSync fs path valid

    let fd = openAppend fs path

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
            writeAndSync fs fd (Codec.toString Codec.sessionEventEnvelope envelope + "\n")
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
