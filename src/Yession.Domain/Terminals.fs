namespace Yession.Domain.Terminals

open Yession.Domain
open Yession.Domain.Terminals

/// Terminals, projected from the event log (Plan 12). Like the conversation, this
/// is a pure fold over ordered events and nothing else — never the doc, never live
/// output. That is what makes the terminal list, its blocks, and their exit codes
/// identical on every replica and after every reload, and it is why there is no way for a
/// client to invent a block: the only constructor is an event the Session Process wrote.
///
/// The bytes a block printed are NOT here. They live in the terminal's transcript
/// (`Transcript.fs`); a block records the transcript range it produced, and a renderer
/// joins the two. The split is the whole design: facts fold, bytes stream.

// The gate a command passes on its way to running is the classifier (Classify.fs) — one
// seam, shared with every other gated act, because nothing about deciding whether an act
// may happen is terminal-shaped.

/// What a client has just learned about how far a terminal's transcript has got.
///
/// The two cases carry different UNITS, and that is the entire reason this type exists.
/// `RecordAt` is an INDEX — the seq a live record arrived at. `AvailableLength` is a COUNT —
/// how many lines the feed says the transcript holds. A client asking "is there something I
/// have not read?" therefore needs `>=` for one and `>` for the other, against the same read
/// position.
///
/// That comparison used to be made at each call site, and one of them made it with the wrong
/// operator: `readPositionOf` is the NEXT unread line, so a live record arriving at exactly
/// that seq IS unread, and `>` skipped it. For a terminal opened mid-session that path is the
/// only trigger to fetch history — the availability hint is sent once, at accept — so the
/// LAST record, the shell's output with nothing following to push the stream ahead, was shown
/// live and never fetched to the store. A reload replayed the command with no output. It only
/// ever failed on a loaded runner, because a slow local drain left the read position behind
/// and hid it.
///
/// So the units live in the type and the rule lives with the cursor. A caller says what it
/// SAW; it does not do arithmetic about somebody else's read position.
type TranscriptSignal =
    /// A live record arrived at this seq. An index into the transcript.
    | RecordAt of seq: int
    /// The feed says the transcript holds this many lines. A count.
    | AvailableLength of length: int

module TranscriptCursor =

    /// Does this signal mean there are records at or after `readPosition` that have not been
    /// read? `readPosition` is the NEXT unread line (`NextSeq = first + lines.Length`), never
    /// the last read one.
    let unread (readPosition: int) (signal: TranscriptSignal) =
        match signal with
        | RecordAt seq -> seq >= readPosition
        | AvailableLength length -> length > readPosition

/// A terminal's screen size in character cells (Plan 13, stage 2b).
///
/// One size per terminal, not one per viewer. A pty has a single size and every peer is
/// looking at the same screen, so a viewer with a smaller viewport scrolls rather than
/// shrinking everyone else's — resizing the terminal down to its smallest viewer is tmux's
/// worst inheritance and the reason a shared session there is unusable on a phone.
type Size = { Cols : int; Rows : int }

module Size =

    /// 80x24: what every terminal has defaulted to since the VT100, and what the transcript
    /// header records when one opens.
    let default' : Size = { Cols = 80; Rows = 24 }

    /// Sizes a terminal can actually be. A zero or negative dimension is not a small
    /// terminal, it is a broken one — and it reaches us from a doc any peer can write.
    let isValid (size: Size) : bool = size.Cols > 0 && size.Rows > 0

    /// How a size is written into a transcript, and read back out of one — `"120x40"`, the
    /// asciicast `"r"` record's payload.
    ///
    /// The pair lives here, with the type, because the two halves run in different processes:
    /// the Session Process writes the record when it resizes a pty, and a browser reads it to
    /// reshape the emulator composing that terminal's screen. A `sprintf` on one side and a
    /// regex on the other is one format in two places, and the side that drifts is the side
    /// nothing round-trips.
    let format (size: Size) : string = sprintf "%dx%d" size.Cols size.Rows

    /// `None` for anything that is not two positive integers around one `x`. A transcript is
    /// replayed by clients that did not write it, and a record that cannot be read is a record
    /// to skip — never a reason to stop reading the rest.
    let parse (text: string) : Size option =
        match text.Split 'x' with
        | [| cols; rows |] ->
            match System.Int32.TryParse cols, System.Int32.TryParse rows with
            | (true, cols), (true, rows) ->
                let size = { Cols = cols; Rows = rows }
                if isValid size then Some size else None
            | _ -> None
        | _ -> None

/// Where a block is in its life.
///
/// `BlockRejected` widens what a block IS, deliberately: a `BlockId` names a proposed
/// command and its outcome, not a process. A refusal shown in line with the commands that
/// did run reads as *"agent: `rm -rf /` — rejected by nick"*; without it the entry simply
/// vanishes from every screen, which is indistinguishable from a bug.
type BlockStatus =
    | BlockRunning
    | BlockFinished of CommandResult
    | BlockRejected of by: ActorRef * reason: string option

/// One executed command and the transcript range it produced.
type Block =
    { BlockId : BlockId
      /// The queue entry this block came from, when it came through a composer (Plan 13,
      /// stage 3b). Projected rather than dropped because it is the handle the agent is given:
      /// a block does not exist until the command runs, so resuming a command that is still
      /// waiting has to be keyed on the request, and this is what joins the two afterwards.
      QueueId : QueueId option
      /// The parties behind the command (Plan 20): who wrote it, whose credential it ran on,
      /// who released it. Carried whole rather than split back into fields, because a
      /// projection that re-spells the value is another place the three can drift apart.
      Authority : Authority
      Command : string
      /// Whether the agent asked for this one in the background (Plan 20, stage 2) — it did
      /// not hold a turn open, and its completion is something the agent is waiting to be
      /// told about. Projected so a surface can SAY so while it runs: work nobody is sitting
      /// in front of is the work most worth marking.
      Background : bool
      /// First transcript line of this block's output.
      FromSeq : int
      /// One past its last transcript line; `None` while it is still running.
      ToSeq : int option
      Status : BlockStatus }

/// One terminal, as the UI knows it.
type TerminalView =
    { TerminalId : TerminalId
      Title : TerminalTitle
      OpenedBy : ActorRef
      /// Which of the session's WorkSandboxes this terminal runs in (Plan 15, stage 2).
      /// Fixed at open, because a terminal IS a shell process inside one sandbox — moving
      /// it would mean killing it, which is a close and an open, and those already exist.
      /// `None` for a terminal attached to a stream somebody else produces (Plan 16, part
      /// D): it runs nowhere this session owns.
      Sandbox : SandboxRef option
      /// Can this terminal's stream be asked for again (Plan 19, step 4)? What decides
      /// whether a closed device terminal offers a way back or merely its recording.
      Renewable : bool
      /// A closed terminal keeps its blocks: the audit outlives the process.
      IsOpen : bool
      /// Why it closed, when it has.
      ClosedReason : string option
      /// Who holds the terminal's stdin, when anyone does (Plan 13, stage 2e). `Some` IS
      /// live mode: there is no separate mode flag, because a mode nobody holds and a lease
      /// nobody holds would be two names for one fact, free to disagree.
      Lease : ActorRef option
      /// Whether the shell has stopped emitting marks (Plan 13, stage 2f). While true the
      /// Process cannot tell when a command starts or finishes here, so the queue is held and
      /// the surface says so — a stall with a name beats a stall.
      IntegrationLost : bool
      /// Blocks in the order they ran.
      Blocks : Block list
      /// Output this terminal produced that the transcript did not keep. Non-zero means
      /// the record has a stated gap.
      DroppedBytes : int }

/// What a terminal's state affords a reader RIGHT NOW (Plan 20, stage 0) — the verbs its row
/// in the terminal list offers.
///
/// Here rather than in the view because it is a rule about a terminal's state, and a rule
/// lives with the state it governs. The same rule used to be spelled out by hand in three
/// templates — "offered only for a terminal that is actually open", "a live terminal's
/// recording is still being written", "shown ONLY when both are true" — each correct, each
/// re-derived, and each testable only by building the whole client and reading HTML back. As
/// one fold it is four booleans the cheap tier pins directly, and "a destructive control is
/// not offered over nothing" stops being a convention three templates happen to remember.
///
/// Stage 1 deleted those three with the strip's own verbs, so this is now the only place
/// that decides.
///
/// Absent verbs are ABSENT, never disabled: a control that mostly refuses teaches people not
/// to press it, and this list's controls have to work the time somebody needs them.
type Affordances =
    { /// End the process. Open terminals only — a "close" on a closed one either does
      /// nothing or reports an error, and both are worse than not being there.
      CanKill : bool
      /// Step back through what a LIVE terminal has recorded so far (Plan 14, stage 7). A
      /// DVR with nothing behind it is a control with nothing to do.
      CanRewind : bool
      /// Play a CLOSED terminal's recording. False with the terminal closed is the stated
      /// gap — the per-terminal cap ate it — which the surface says rather than opening an
      /// empty player.
      CanReplay : bool
      /// Ask the provider for the stream again (Plan 19, step 4). Closed, and its source
      /// said asking again is safe; a shell terminal is never renewable, because a second
      /// shell is a second terminal and opening one already exists.
      CanReattach : bool
      /// Whether the recording is the ONLY read this terminal has, so its panel opens
      /// playing rather than offering a way to.
      ///
      /// A closed terminal that ran commands has two reads of one history — the blocks, and
      /// the recording — and showing both at once made the second redundant wherever the
      /// first said everything: a command and its result, printed, with a player of the same
      /// two lines under it. So the blocks are the read and the recording is a destination.
      ///
      /// A terminal with no blocks has no such first read. A source that could not be
      /// instrumented (`SourceCapabilities.CanInstrument`) never mints one, and neither does
      /// a shell that only ever held a lease — in both the whole history is in the recording,
      /// and an empty block list is not a read, it is a `$`. Asked of the BLOCKS rather than
      /// of the source, because blocks are what the other read is made of: a source flag
      /// would answer "could this have had blocks" about a terminal that has none.
      ReplayIsTheRead : bool
      /// `ReplayIsTheRead`'s live twin: whether the SCREEN is the only read this terminal
      /// has, so its panel shows one rather than a list of commands.
      ///
      /// The screen used to be shown only while somebody held the LEASE. That is right for a
      /// shell — a held lease there means a program has taken the screen, and letting go
      /// brings the blocks back — and wrong for a device, which has no blocks to come back
      /// to. An attached serial port nobody had taken rendered an empty block list beside a
      /// stream that was arriving the whole time, and the way to see anything was to claim
      /// the keyboard.
      ///
      /// Watching is not typing. This is what a reader gets; the keyboard stays the lease's.
      ScreenIsTheRead : bool }

module Affordances =

    /// `recorded` is whether this READER holds anything of the terminal's recording. The one
    /// input that is not a fact about the terminal, and it cannot be: a recording lives in
    /// the transcript store, so no fold over the event log can answer it — which is exactly
    /// why it is a named parameter rather than something this module reaches for.
    let ofView (recorded: bool) (view: TerminalView) : Affordances =
        { CanKill = view.IsOpen
          CanRewind = view.IsOpen && recorded
          CanReplay = not view.IsOpen && recorded
          // Not gated on `recorded`, and that is the point of asking the provider rather than
          // the store: a terminal whose recording the cap ate can still have a live device on
          // the other end, and refusing the way back because the RECORD is gone would answer
          // a question nobody asked.
          CanReattach = not view.IsOpen && view.Renewable
          ReplayIsTheRead = not view.IsOpen && recorded && List.isEmpty view.Blocks
          // The sandbox AND the blocks, and both earn their place. `None` is a stream
          // somebody else produces, which is what makes the screen the whole of it — a shell
          // that has not run its first command yet is still a shell, and its read is the
          // blocks it is about to have. And a source that declared `instrument` gets blocks
          // while still having no sandbox, so the sandbox alone would take the block read
          // away from exactly the source that has one.
          ScreenIsTheRead = view.IsOpen && Option.isNone view.Sandbox && List.isEmpty view.Blocks }

/// Every terminal this session has had, in the order they were opened.
type Projection = { Terminals : TerminalView list }

module Projection =

    let empty : Projection = { Terminals = [] }

    let private updateTerminal (id: TerminalId) (f: TerminalView -> TerminalView) (proj: Projection) =
        { Terminals = proj.Terminals |> List.map (fun t -> if t.TerminalId = id then f t else t) }

    let private updateBlock (id: BlockId) (f: Block -> Block) (view: TerminalView) =
        { view with Blocks = view.Blocks |> List.map (fun b -> if b.BlockId = id then f b else b) }

    /// Fold one event into the projection. Only terminal events matter; everything else
    /// passes through, so this composes with the other folds over the same page.
    let applyEvent (proj: Projection) (event: SessionEvent) : Projection =
        match event with
        | SessionEvent.TerminalOpened e ->
            // Re-opening an id that already exists is not a second terminal: ids are minted
            // by the Process, so this can only be a replayed event, and the fold must be
            // idempotent for the offset-gated page reads to stay safe.
            if proj.Terminals |> List.exists (fun t -> t.TerminalId = e.TerminalId) then proj
            else
                { Terminals =
                    proj.Terminals
                    @ [ { TerminalId = e.TerminalId
                          Title = e.Title
                          OpenedBy = e.OpenedBy
                          Sandbox = e.Sandbox
                          Renewable = e.Renewable
                          IsOpen = true
                          ClosedReason = None
                          Lease = None
                          IntegrationLost = false
                          Blocks = []
                          DroppedBytes = 0 } ] }
        | SessionEvent.TerminalClosed e ->
            // The lease goes with the terminal. A closed terminal has no stdin to hold, and
            // a holder left standing on one would render as "nick is typing" for ever.
            proj
            |> updateTerminal e.TerminalId (fun t ->
                { t with IsOpen = false; ClosedReason = Some e.Reason; Lease = None })
        | SessionEvent.TerminalLeaseTaken e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with Lease = Some e.By })
        | SessionEvent.TerminalLeaseReleased e ->
            // Clear only if the holder is still the one this release names. A steal is two
            // events — the old lease ending and the new one starting — and this guard is what
            // makes the fold independent of which order they are appended in: a release
            // naming someone who no longer holds it is stale, and acting on it would drop the
            // lease the take beside it just granted.
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Lease = Some e.Was then { t with Lease = None } else t)
        | SessionEvent.TerminalBlockStarted e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Blocks |> List.exists (fun b -> b.BlockId = e.BlockId) then t
                else
                    { t with
                        Blocks =
                            t.Blocks
                            @ [ { BlockId = e.BlockId
                                  QueueId = e.QueueId
                                  Authority = e.Authority
                                  Command = e.Command
                                  Background = e.Background
                                  FromSeq = e.FromSeq
                                  ToSeq = None
                                  Status = BlockRunning } ] })
        | SessionEvent.TerminalBlockCompleted e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                t |> updateBlock e.BlockId (fun b -> { b with ToSeq = Some e.ToSeq; Status = BlockFinished e.Result }))
        | SessionEvent.TerminalCommandRejected e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Blocks |> List.exists (fun b -> b.BlockId = e.BlockId) then t
                else
                    { t with
                        Blocks =
                            t.Blocks
                            @ [ { BlockId = e.BlockId
                                  QueueId = Some e.QueueId
                                  // A command that never ran was never anybody's wait.
                                  Background = false
                                  // Who refused it is on the status rather than smuggled
                                  // in here.
                                  Authority = Authority.ofAuthor e.Author
                                  Command = e.Command
                                  // An EMPTY range, not a missing one: a command that never
                                  // ran produced no output, so every reader that slices
                                  // [From, To) gets nothing without a special case.
                                  FromSeq = 0
                                  ToSeq = Some 0
                                  Status = BlockRejected (e.RejectedBy, e.Reason) } ] })
        | SessionEvent.TerminalIntegrationLost e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with IntegrationLost = true })
        | SessionEvent.TerminalIntegrationRestored e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with IntegrationLost = false })
        | SessionEvent.TerminalTranscriptTruncated e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with DroppedBytes = t.DroppedBytes + e.DroppedBytes })
        | _ -> proj

    let tryFind (id: TerminalId) (proj: Projection) : TerminalView option =
        proj.Terminals |> List.tryFind (fun t -> t.TerminalId = id)

    /// The terminals still open, in open order — what the panel lists.
    let openTerminals (proj: Projection) : TerminalView list =
        proj.Terminals |> List.filter (fun t -> t.IsOpen)

    /// The block currently running in a terminal, if any. At most one: the drain runs a
    /// terminal's queue one command at a time, which is what makes a shell's working
    /// directory and environment mean anything from one command to the next.
    let runningBlock (view: TerminalView) : Block option =
        view.Blocks |> List.tryFind (fun b -> b.Status = BlockRunning)

/// What the emulator's alt-screen state proposes doing about the lease (Plan 13, stage 2e).
///
/// "The flip is detected, not configured": a TUI taking the screen is the universal signal
/// that a program, not a prompt, owns the terminal, and the person whose command started it
/// is the person who now needs to type into it. This is the whole policy, deliberately one
/// pure function over the emulator's state so that shipping it, tuning it, or turning it off
/// is a one-line change rather than an excavation.
type Flip =
    /// Give the lease to this actor — a block became a TUI and its author needs the keyboard.
    | FlipToLive of ActorRef
    /// The TUI exited and nobody claimed the terminal by hand: back to block mode.
    | FlipToBlock
    | FlipNothing

module Flip =

    /// `altScreen` is the emulator's current buffer; `holder` the lease as it stands;
    /// `autoHeld` whether that holder got it from a previous `FlipToLive` rather than by
    /// asking; `runningAuthor` the author of the block running now, if one is.
    ///
    /// Three rules, and the second two are what "detection PROPOSES the mode" means:
    ///
    ///   * A held lease is never overridden by detection. A peer who took the terminal owns
    ///     it until they release it or someone steals it — a program exiting is not either.
    ///   * Detection only ever RELEASES what detection took. Otherwise leaving `vim` would
    ///     yank the keyboard from a peer who had taken the terminal explicitly and happened
    ///     to run an editor in it.
    ///   * The flip follows the AUTHOR, and the agent is one (Plan 20, stage 6). This rule
    ///     used to read "an agent-authored block does not flip — live mode is human-only",
    ///     which was true of a session where the agent had no hands: live mode was a browser
    ///     surface, so handing it a terminal would have handed it to nobody. Plan 19 gave it
    ///     `write_terminal`/`read_terminal`, and what the exception left behind was a wedge —
    ///     an agent command that takes the screen waits for a keystroke nobody is allowed to
    ///     send, so its block never finishes and the queue behind it never moves. Nothing
    ///     flips to `SessionProcess`, `System` or a repo's file, and that is not policy
    ///     either: nothing in the session can type as any of them.
    let propose
        (altScreen: bool)
        (holder: ActorRef option)
        (autoHeld: bool)
        (runningAuthor: ActorRef option)
        : Flip =
        match altScreen, holder with
        | true, None ->
            match runningAuthor with
            | Some (PeerRef _ as author) | Some (UserRef _ as author) | Some (Agent as author) -> FlipToLive author
            | Some SessionProcess | Some System | Some (Configured _) | None -> FlipNothing
        | true, Some _ -> FlipNothing
        | false, Some _ when autoHeld -> FlipToBlock
        | false, _ -> FlipNothing

/// One block an agent turn is told the outcome of (Plan 13, stage 3a).
///
/// Terminal events fold into `Projection` and deliberately NOT into the
/// conversation — a command someone ran is not something someone said. That is right for
/// the chat log and wrong for the agent, whose context is built from the conversation, so
/// without this it cannot see the result of anything it queued, on that turn or any later
/// one. Which is the substantive reason it reaches for a private execution path instead.
type BlockDigest =
    { TerminalId : TerminalId
      /// The terminal's title, so the agent can name the place rather than an opaque id.
      Title : TerminalTitle
      BlockId : BlockId
      /// Who wrote the command — the agent's own, or someone else's it should know ran.
      Author : ActorRef
      Command : string
      Status : BlockStatus
      /// The tail of what the block printed, capped. All of it stays in the transcript;
      /// this is the part that fits in a context window.
      OutputTail : string
      /// Characters of output the tail leaves out. Stated rather than silently elided: a
      /// model that cannot tell a short output from a truncated one will confidently
      /// describe the wrong thing.
      Elided : int }

/// What an agent turn is told about the terminals since it last ran (Plan 13, stage 3a).
module Digest =

    /// Characters of output tail kept per block. The transcript keeps the rest, and a
    /// block's full range travels with it, so nothing here is the only copy.
    let tailCap = 2000

    /// The blocks whose start or completion fell after the PREVIOUS turn began.
    ///
    /// No stored cursor is needed, and that is a property of when the page is read rather
    /// than a trick: an agent turn's context is built from a page read BEFORE that turn
    /// appends its own `AgentTurnStarted`, so resetting on every `AgentTurnStarted` in the
    /// page leaves exactly what moved since the previous one.
    ///
    /// Completion counts as movement, not just the start. A block that began before the
    /// last turn and finished during it is precisely the case the agent is waiting on —
    /// reporting only newly-started blocks would drop every outcome it actually asked for.
    let window (events: SessionEvent list) : Set<string> =
        events
        |> List.fold
            (fun acc event ->
                match event with
                | SessionEvent.AgentTurnStarted _ -> Set.empty
                | SessionEvent.TerminalBlockStarted b -> Set.add (BlockId.value b.BlockId) acc
                | SessionEvent.TerminalBlockCompleted b -> Set.add (BlockId.value b.BlockId) acc
                | _ -> acc)
            Set.empty

    /// Assemble the digest: every in-window block, in the order it ran, with a bounded
    /// tail of what it printed. `readOutput` is handed the block's transcript range —
    /// `None` for a running block, which has no end yet and reads to whatever the terminal
    /// has so far.
    let build
        (readOutput: TerminalId -> int -> int option -> string)
        (window: Set<string>)
        (proj: Projection)
        : BlockDigest list =
        [ for terminal in proj.Terminals do
            for block in terminal.Blocks do
                if Set.contains (BlockId.value block.BlockId) window then
                    let output = readOutput terminal.TerminalId block.FromSeq block.ToSeq
                    let elided = max 0 (output.Length - tailCap)
                    { TerminalId = terminal.TerminalId
                      Title = terminal.Title
                      BlockId = block.BlockId
                      Author = Authority.author block.Authority
                      Command = block.Command
                      Status = block.Status
                      OutputTail = (if elided > 0 then output.Substring elided else output)
                      Elided = elided } ]
