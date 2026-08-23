namespace Yession.Domain

/// A page of events read from the log. The page is the unit of deterministic, offset-based
/// reads. See docs/design.md §1.
type EventPage<'event> =
    { Events     : EventEnvelope<'event> list
      LastOffset : EventOffset option
      IsEnd      : bool }

/// The result of appending an event: the monotonic offset assigned to it.
type AppendResult =
    { Offset : EventOffset }

/// How much of the append-only log one HTTP answer carries (Plan 20).
///
/// The log is read by CURSOR: a client sends the offset it has folded through and the
/// server answers at an address naming the bounds it chose — `events/{first}-{last}`.
/// Those bounds never move, so that answer is the same bytes for ever and a client can
/// keep it, tail included. The client computes none of this: it holds an offset and
/// stores what it is given under the address it was given.
///
/// This module used to map offsets to fixed chunk indices, and the index was the problem:
/// `events/3` meant *whatever chunk 3 holds now*, which grows, so the newest events could
/// never be kept by anyone.
///
/// Two nearer alternatives were rejected for one reason each. Prefix ranges (`0-99`, `0-136`,
/// `0-137` …) are equally immutable and store every event once per length at which it was ever
/// observed; disjoint ranges cost each event exactly once, because each cursor request begins
/// one past the last event folded. A `Link: rel=next` chain with a mutable `/events/head`
/// entry point duplicated what the answers already carry — their own offsets, which is what a
/// replay orders by — and what a cursor answers for free with `204`.
module EventChunk =

    /// The most events one answer carries. Server-side only — no client ever names a
    /// range, so nothing outside the session process needs to know this number.
    let size = 100
