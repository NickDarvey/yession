namespace Yession.Domain

/// The per-terminal transcript: the durable, append-only record of everything a terminal
/// produced and everything typed into it (docs/plans/12).
///
/// It is deliberately NOT the event log. The event log records durable *facts* every
/// client folds — a terminal that prints a gigabyte contributes four events there. The
/// bytes themselves go here, to a sidecar file per terminal, because raw output is
/// unmergeable, unfoldable, and unbounded, and putting it in the log would make every
/// client pay the size of what was printed rather than the size of what happened.
///
/// The format is **asciicast v2** (asciinema's): a JSON header line followed by one JSON
/// array per record. That is a deliberate choice of an existing, documented, replayable
/// format over a bespoke one — an operator can hand a `.cast` file to any asciinema
/// player and watch the session back, which is what makes "audited" mean something
/// outside this codebase.
///
/// One extension: asciicast defines `"o"` (output), `"i"` (input) and `"r"` (resize);
/// this adds `"e"` for stderr. A pty cannot tell the two streams apart (that is what a
/// tty *is*), but the piped block runner can, and discarding a distinction the capture
/// actually has — in the one record whose purpose is fidelity — would be the wrong trade.
/// A player that does not know `"e"` skips it; ours renders it.
///
/// The audit trail is the RAW stream, never the rendered screen. ANSI can move the cursor
/// and overwrite what was printed a moment ago, so what a terminal *displays* is a
/// projection with lossy history; what it *emitted* is the record.
type TranscriptKind =
    | TranscriptOutput
    | TranscriptStderr
    | TranscriptInput
    | TranscriptResize

module TranscriptKind =

    /// The asciicast event code.
    let code =
        function
        | TranscriptOutput -> "o"
        | TranscriptStderr -> "e"
        | TranscriptInput -> "i"
        | TranscriptResize -> "r"

    let parse (raw: string) : TranscriptKind option =
        match raw with
        | "o" -> Some TranscriptOutput
        | "e" -> Some TranscriptStderr
        | "i" -> Some TranscriptInput
        | "r" -> Some TranscriptResize
        | _ -> None

/// One transcript record: `[At, code, Data]` on the wire. `At` is seconds since the
/// header's timestamp — asciicast's relative clock, which is what makes a replay
/// independent of when it is replayed.
type TranscriptRecord =
    { At : float
      Kind : TranscriptKind
      Data : string }

/// The asciicast header — the transcript's first line, written once when the terminal
/// opens.
type TranscriptHeader =
    { Width : int
      Height : int
      /// Unix seconds at which the terminal opened; every record's `At` is relative to it.
      Timestamp : int64 }

/// A transcript file is a sequence of these, one per line, header first.
type TranscriptLine =
    | TranscriptHeaderLine of TranscriptHeader
    | TranscriptRecordLine of TranscriptRecord

/// Fixed-size chunking of a transcript, for HTTP-cacheable reads — the same construction
/// `EventChunk` applies to the event log, for the same reason and with the same payoff.
///
/// A transcript is append-only and its chunk bounds are fixed forever, so a chunk holding
/// all `size` lines is IMMUTABLE and can be cached hard; only the growing tail chunk must
/// be revalidated. Concatenating chunk 0, 1, 2 … reproduces the file byte for byte, which
/// is why the header is simply line 0 rather than a value served beside the chunks: any
/// prefix of chunks is a valid `.cast` file, so replaying half a transcript needs no
/// special case.
///
/// A **sequence number** in this module means a LINE INDEX in that file — the same
/// currency the event log's offsets are in, so the catch-up logic is the same shape:
/// hints and live records carry a seq, the client keeps a high-water mark, and anything
/// at or below it is skipped.
module TranscriptChunk =

    /// Lines per chunk. Fixed forever once shipped: chunk URLs are cache keys.
    let size = 500

    /// Cached-full-chunk lifetime: 3 days, matching `EventChunk` — a session resumed over
    /// a weekend replays its terminals from the browser cache too.
    let private maxAgeSeconds = 259200

    /// The chunk containing line `seq`.
    let indexOf (seq: int) : int = seq / size

    /// The first line index of chunk `index`.
    let firstSeq (index: int) : int = index * size

    /// `private`, like the event chunks: transcripts sit behind the session's per-user
    /// authorization, so the browser's own cache still serves them and shared caches never
    /// see them.
    let cacheControl (isFull: bool) : string =
        if isFull then sprintf "private, max-age=%d, immutable" maxAgeSeconds
        else "no-store"
