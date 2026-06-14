namespace Yession.Domain

/// A page of events read from the log. The page is the unit of deterministic, offset-based
/// reads. See docs/plans/00-init/01-event-log.md and docs/design.md §1.
type EventPage<'event> =
    { Events     : EventEnvelope<'event> list
      LastOffset : EventOffset option
      IsEnd      : bool }

/// The result of appending an event: the monotonic offset assigned to it.
type AppendResult =
    { Offset : EventOffset }
