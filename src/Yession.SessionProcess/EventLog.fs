namespace Yession.SessionProcess

open System
open Yession.Domain

/// Function-shaped event-log capabilities. The Session Process is the only caller of
/// these. See docs/design.md §1 ("Durable facts are events", "Composition at the top")
/// and docs/plans/00-init/01-event-log.md.
///
/// `ReadEvents` returns a single page of at most `limit` events after the given offset.
/// Callers page by re-reading from `page.LastOffset` until `page.IsEnd`. This single-page
/// shape (rather than a stream) keeps the capability portable to the Fable/Node runtime
/// and mirrors the `ReadEventsAfter(after, limit)` / `EventsPage` transport frames.
type AppendEvent<'event> = ActorRef -> 'event -> Async<AppendResult>
type ReadEvents<'event> = EventOffset option -> int -> Async<EventPage<'event>>

/// The append-only event log, with its storage implementation hidden. Callers depend on
/// these functions, never on the representation.
type EventLog<'event> =
    { Append : AppendEvent<'event>
      Read   : ReadEvents<'event> }

/// An in-memory event log. Phase 1 storage; the `EventLog` interface it returns does not
/// expose the in-memory representation, so storage is replaceable without changing callers.
///
/// Works on both .NET (where appends may race across threads, guarded by a lock) and the
/// single-threaded Fable/Node runtime (where the lock is unnecessary and elided).
module InMemoryEventLog =

    [<Literal>]
    let DefaultPageSize = 100

    /// Run `f` under the gate. On .NET this is a real lock so concurrent appends stay
    /// monotonic; under Fable (single-threaded JS) no lock is needed.
    let inline private withLock (gate: obj) (f: unit -> 'a) : 'a =
#if FABLE_COMPILER
        ignore gate
        f ()
#else
        lock gate f
#endif

    /// Create an append-only, in-memory event log for a single session.
    ///
    /// - `clock` stamps each appended envelope (injected so time stays at the boundary).
    let create
        (sessionId: SessionId)
        (clock: unit -> DateTimeOffset)
        : EventLog<'event> =

        let gate = obj ()
        let events = ResizeArray<EventEnvelope<'event>>()

        let append (actor: ActorRef) (event: 'event) : Async<AppendResult> =
            async {
                return
                    withLock gate (fun () ->
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

        let read (after: EventOffset option) (limit: int) : Async<EventPage<'event>> =
            async {
                return
                    withLock gate (fun () ->
                        // Snapshot under the gate so the page is deterministic against the
                        // log state observed at read time.
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

                        { Events = List.ofArray pageEvents
                          LastOffset = lastOffset
                          // The page is the tail when it contains everything still
                          // available after `after`.
                          IsEnd = pageEvents.Length = selected.Length })
            }

        { Append = append
          Read = read }
