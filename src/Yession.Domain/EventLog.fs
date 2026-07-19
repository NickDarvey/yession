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

/// Fixed-size chunking of the append-only log, for HTTP-cacheable reads: chunk `n`
/// covers offsets `[n*Size, (n+1)*Size)`. Because the log is append-only, a chunk that
/// has all `Size` events is IMMUTABLE — its URL can be cached hard by any HTTP cache
/// (the browser's included); only the growing tail chunk must never be cached. That
/// makes the browser's own cache the client-side event store: cold loads replay full
/// chunks from disk and fetch only the tail.
module EventChunk =

    /// Events per chunk. Fixed forever once shipped: chunk URLs are cache keys.
    let size = 100

    /// Cached-full-chunk lifetime: 3 days, so a session resumed over a weekend still
    /// replays from the browser cache.
    let private maxAgeSeconds = 259200

    /// The chunk containing `offset`.
    let indexOf (offset: int64) : int = int (offset / int64 size)

    /// The first offset of chunk `index`.
    let firstOffset (index: int) : int64 = int64 index * int64 size

    /// The `Cache-Control` value for a chunk response. Full chunks are immutable by
    /// construction (append-only log, fixed chunk bounds); a partial chunk is still
    /// growing and must be revalidated every time. `private` because chunks now sit
    /// behind per-user authorization: the BROWSER cache still serves them (offline
    /// replay keeps working); only shared caches are excluded.
    let cacheControl (isFull: bool) : string =
        if isFull then sprintf "private, max-age=%d, immutable" maxAgeSeconds
        else "no-store"
