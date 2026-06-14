namespace Yession.SessionProcess

open System
open FSharp.Control
open Yession.Domain

/// Function-shaped event-log capabilities. The Session Process is the only caller of
/// these. See docs/design.md §1 ("Durable facts are events", "Composition at the top")
/// and docs/plans/00-init/01-event-log.md.
type AppendEvent<'event> = ActorRef -> 'event -> Async<AppendResult>
type ReadEvents<'event> = EventOffset option -> AsyncSeq<EventPage<'event>>

/// The append-only event log, with its storage implementation hidden. Callers depend on
/// these functions, never on the representation.
type EventLog<'event> =
    { Append : AppendEvent<'event>
      Read   : ReadEvents<'event> }

/// An in-memory event log. Phase 1 storage; the `EventLog` interface it returns does not
/// expose the in-memory representation, so storage is replaceable without changing callers.
module InMemoryEventLog =

    [<Literal>]
    let DefaultPageSize = 100

    /// Create an append-only, in-memory event log for a single session.
    ///
    /// - `clock` stamps each appended envelope (injected so time stays at the boundary).
    /// - `pageSize` bounds the number of events returned per read page.
    let create
        (sessionId: SessionId)
        (clock: unit -> DateTimeOffset)
        (pageSize: int)
        : EventLog<'event> =

        let gate = obj ()
        let events = ResizeArray<EventEnvelope<'event>>()

        let append (actor: ActorRef) (event: 'event) : Async<AppendResult> =
            async {
                return
                    lock gate (fun () ->
                        // Append-only: the next offset is the current count. Monotonicity
                        // follows directly because events are never removed.
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

                        events.Add envelope
                        { Offset = offset })
            }

        let read (after: EventOffset option) : AsyncSeq<EventPage<'event>> =
            // Snapshot under the lock so a single read is deterministic against the log
            // state observed at iteration start.
            let snapshot = lock gate (fun () -> events.ToArray())
            let afterValue = after |> Option.map EventOffset.value

            let selected =
                snapshot
                |> Array.filter (fun e ->
                    match afterValue with
                    | Some n -> EventOffset.value e.Offset > n
                    | None -> true)

            asyncSeq {
                if selected.Length = 0 then
                    // A single empty terminal page so readers get an unambiguous end signal.
                    yield { Events = []; LastOffset = None; IsEnd = true }
                else
                    let chunks = Seq.chunkBySize pageSize selected |> Seq.toArray
                    for i in 0 .. chunks.Length - 1 do
                        let chunk = chunks.[i]
                        yield
                            { Events = List.ofArray chunk
                              LastOffset = Some (Array.last chunk).Offset
                              IsEnd = (i = chunks.Length - 1) }
            }

        { Append = append
          Read = read }
