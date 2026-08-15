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

/// The screen as it stood immediately BEFORE transcript line `Seq` (Plan 14, stage 3).
///
/// Replaying a slice of a transcript is wrong twice, and both look fine in a demo:
///
///   * **Times are absolute.** asciicast timestamps are relative to the start of the FILE,
///     so a block that ran forty minutes in makes the player sit idle for forty minutes
///     before its first frame. `TranscriptReplay.range` rebases them.
///   * **Screen state is path-dependent.** What a terminal shows at line 500 is a function
///     of every byte before it — colours set earlier, cursor position, scroll region,
///     whether something entered the alternate screen. Replaying a slice into a fresh VT is
///     *approximately* right for ordinary command output and wrong exactly when it matters,
///     and it is not close at all for a lease stretch, which by definition begins with a
///     program already owning the screen.
///
/// So the Session Process serializes the emulator at each range START — every block's
/// `FromSeq`, every lease stretch's — and a ranged replay becomes *header + one synthesized
/// output record that paints this + the rebased range*, which is a valid asciicast the stock
/// player renders with no modification.
///
/// Note what this is **not** for. Keyframes are usually a seek-performance trick, and that
/// was the weaker case: the player rebuilds VT state by re-feeding from `t=0`, which is
/// milliseconds for an ordinary recording. The reason to keep them is *correctness of a
/// ranged replay*, and their cost is bounded by the number of blocks and leases rather than
/// by output volume — the same argument that keeps input and resize records uncapped while
/// output is capped.
///
/// They live in a SIDECAR, never in the `.cast`. Plan 13 bought a standard, replayable
/// format on purpose; putting a private record type in the file spends that.
type TranscriptKeyframe =
    { /// The transcript line this paints the screen for. A range `[from, to)` wants the
      /// keyframe whose `Seq` is exactly `from`.
      Seq : int
      /// The terminal's size at that moment. Carried because a resize BEFORE the range
      /// changed the screen's shape, and the header records only the size the terminal
      /// OPENED at — a ranged replay under the wrong geometry rewraps every line in it.
      Cols : int
      Rows : int
      /// The ANSI that repaints the screen, from the same serializer `TerminalSnapshot`
      /// uses to bring a joining peer up to date. One serializer, so a replayed screen and
      /// a joined screen cannot disagree.
      Screen : string }

/// What a transcript keeps (Plan 13, stage 3d; Plan 14, stage 0).
///
/// The shape of this is forced by one fact: **a line index IS a sequence number**. It is what
/// `TerminalBlockStarted.FromSeq` and `TerminalBlockCompleted.ToSeq` point at, what
/// `TranscriptChunk.firstSeq` slices on, and what every chunk URL is keyed by. So compaction
/// that removes lines from the front or the middle would renumber everything after it,
/// invalidating every block range in the event log and every cached chunk at once. A rolling
/// window over a live transcript is therefore not available, however natural it sounds.
///
/// What is available is **a ceiling while the terminal is live**. Past the cap, output stops
/// being kept. What is given up is the NEWEST output rather than the oldest, which is the
/// opposite of a window and the only direction that preserves numbering — the same idea the
/// per-block cap already applies, across a terminal's whole life. It records what was lost as
/// `TerminalTranscriptTruncated`, which already means exactly "output this terminal produced
/// and the transcript did not keep": a gap in an audit trail is a stated fact, never a silent
/// one.
///
/// **A recording lives as long as its session does** (Plan 14, stage 0). There is no age at
/// which a closed terminal's transcript is deleted, because the chat now carries a permanent,
/// tappable item for every block and every lease stretch — and a chip whose recording a timer
/// deleted underneath it is a dead end. The cost is stated rather than discovered: the
/// per-terminal cap is the only ceiling left, so a session's transcript floor is
/// `outputCap` × terminals and it never shrinks.
module TranscriptRetention =

    /// Bytes of OUTPUT one terminal's transcript keeps across its whole life. Generous enough
    /// that an ordinary session never meets it, and finite so a runaway `yes` cannot fill a
    /// disk between the per-block cap and the end of the session.
    let outputCap = 64 * 1024 * 1024

    /// What survives the cap, and what did not. Both, because the boundary record is PARTLY
    /// kept — a `Result` could carry one or the other and would have to lie about that one.
    type Admission =
        { /// The part written to the transcript. Empty once the cap is met.
          Keep : string
          /// Characters dropped, for `TerminalTranscriptTruncated`.
          Dropped : int }

    /// What to keep of an incoming output record, given how much this terminal's transcript
    /// has kept already.
    ///
    /// Only OUTPUT is capped. Input and resize records are the audit's spine — what the Process
    /// wrote, and what shape the screen was — and they are tiny, bounded by the number of
    /// commands rather than by what any of them printed. Dropping those to save space would
    /// give up the part that answers questions.
    let admit (kept: int) (incoming: string) : Admission =
        let room = outputCap - kept
        if room <= 0 then { Keep = ""; Dropped = incoming.Length }
        elif incoming.Length <= room then { Keep = incoming; Dropped = 0 }
        else { Keep = incoming.Substring (0, room); Dropped = incoming.Length - room }

/// How much of a terminal's append-only transcript one HTTP answer carries (docs/plans/22)
/// — `EventChunk` for the terminal feed, and the same construction for the same reason.
///
/// A transcript is read by CURSOR: a client sends the line it has folded through and the
/// server answers at an address naming the bounds it chose — `terminals/{t}/{first}-{last}`.
/// Line index IS sequence number for ever, so those bounds never move, that answer is the
/// same bytes for ever, and a client can keep it — tail included.
///
/// A **sequence number** here means a LINE INDEX in that file, the same currency the event
/// log's offsets are in: hints and live records carry a seq, the client keeps a high-water
/// mark, and anything at or below it is skipped. The header sits at line 0 and so arrives
/// with the first answer, which is what keeps any prefix of answers a valid `.cast`.
///
/// This module used to map a seq to a fixed chunk index, and the index was the problem:
/// `terminals/{t}/3` meant *whatever chunk 3 holds now*, which grows, so the newest lines
/// could never be kept by anyone.
module TranscriptChunk =

    /// The most lines one answer carries. Named by the SERVER when it bounds a range, and
    /// by the router when it refuses an over-long one — never by a client choosing an
    /// address, because a client never chooses one.
    let size = 500
