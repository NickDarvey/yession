namespace Yession.SessionProcess

open System
open Yession.Domain

/// One terminal's durable transcript, as a capability (docs/plans/12). The Session
/// Process appends to it BEFORE broadcasting a record, so a dropped frame costs latency
/// and never the record — the same "durability before visibility" rule the event log and
/// the doc sidecar are written under.
///
/// Function-shaped, so the file-backed implementation lives at the composition boundary
/// (`app/TranscriptStore.fs`) and the tests drive an in-memory one through the identical
/// contract.
type Transcript =
    { /// Append one record, returning the line index it landed at. That index is the
      /// record's sequence number everywhere else — in the live frame, in the block's
      /// `FromSeq`/`ToSeq`, and in the HTTP chunk a client fetches.
      Append : TranscriptRecord -> int
      /// The line index the next append will use — i.e. how long the transcript is.
      NextSeq : unit -> int }

/// Open (or reopen) a terminal's transcript. The header is written once, when the file is
/// created; reopening an existing transcript appends to it, because a transcript outlives
/// the process that wrote it and an audit trail that restarts at zero is not one.
type OpenTranscript = TerminalId -> TranscriptHeader -> Transcript

/// Read back what a terminal printed, over a half-open line range — `None` as the end
/// meaning "to whatever it has now", which is what a still-running block has.
///
/// Separate from `Transcript` rather than a method on it, because the two have opposite
/// shapes: appends are hot, per-terminal and held open, while reads are rare, bounded and
/// addressed by id (a digest tail, a chunk request). Giving the append path a read it does
/// not use would make every writer carry a reader it must implement.
type ReadTranscript = TerminalId -> int -> int option -> TranscriptRecord list

/// One terminal's screen, as the Session Process keeps it (Plan 13, stage 2b).
///
/// The transcript is the audit trail and the screen is not: ANSI moves the cursor and
/// overwrites what was printed a moment ago, so what a terminal DISPLAYS is a projection
/// with lossy history while what it EMITTED is the record. Both are wanted, for different
/// questions — "what happened here" is the transcript, "what does it look like right now"
/// is this.
///
/// A capability rather than a concrete type because it is a real terminal emulator
/// (`@xterm/headless`) at the composition boundary, and because that keeps the Process's
/// own logic free of a JS dependency it would otherwise have to be tested around.
type Emulator =
    { /// Feed output bytes, exactly as they were written to the transcript.
      Write : string -> unit
      /// The screen and its scrollback, serialized — what a joining peer is sent so it can
      /// render the terminal without replaying every byte that ever reached it.
      ///
      /// Async because the emulator parses writes asynchronously: reading the screen without
      /// waiting for what has been fed to be applied returns a screen that has not been
      /// drawn yet, which is not a snapshot of anything.
      Serialize : unit -> Async<string>
      Resize : int -> int -> unit
      /// Subscribe to alternate-screen transitions — `true` on entry, `false` on exit.
      ///
      /// This is what "the flip is detected, not configured" reads: a TUI taking the screen is
      /// the universal signal that a program rather than a prompt owns the terminal. Read off
      /// the emulator's buffer rather than parsed out of the byte stream, because the emulator
      /// is already the thing that knows — a second DECSET 1049 parser would be a second
      /// interpretation of ANSI, free to disagree with the screen every peer is looking at.
      OnAltScreen : (bool -> unit) -> unit
      Dispose : unit -> unit }

/// Open an emulator of a given size (cols, rows).
type OpenEmulator = int -> int -> Emulator

module Emulator =

    /// An emulator that keeps nothing. For a host with no emulator available: terminals
    /// still run, and the only thing missing is the join snapshot — the same declare-and-skip
    /// honesty a backend without a pty gets.
    let none : Emulator =
        { Write = ignore
          Serialize = fun () -> async { return "" }
          Resize = fun _ _ -> ()
          // No emulator, no alt-screen signal, and therefore no auto-flip: a terminal here
          // still runs blocks and can still be taken by hand. Detection is the part that
          // needs a screen, and it is the part that declares itself missing.
          OnAltScreen = ignore
          Dispose = ignore }

module Transcript =

    /// What a range of records PRINTED. Input records are skipped — the command is already
    /// carried on the block, and echoing it back into its own output would have a reader
    /// count it twice — and a resize is not output at all.
    let printed (records: TranscriptRecord list) : string =
        records
        |> List.filter (fun r -> r.Kind = TranscriptOutput || r.Kind = TranscriptStderr)
        |> List.map (fun r -> r.Data)
        |> String.concat ""

/// The terminal queue's drain decision, as a pure function (Plan 13). The Session Process
/// is the single consumer of the terminal queue exactly as it is of the message queue,
/// and this is the whole policy: what runs next, and what is merely left over.
///
/// Two rules carry the design:
///
///   * **One block at a time per terminal.** A shell's working directory, environment and
///     history are consequences of what ran before, so running a terminal's second queued
///     command while its first is still going would make the queue's order meaningless.
///     Terminals are independent of each other, though — a slow build in one does not
///     hold up another.
///   * **Approval gates the HEAD, and the head alone.** If the entry at the front needs an
///     approval it has not got, the terminal waits — it does not skip ahead to an approved
///     entry behind it. Reordering the queue is a CRDT write anyone can make; silently
///     reordering EXECUTION because of an approval state is not something a person asked
///     for, and in a shell it is the difference between `cd build` then `rm -rf *` and
///     those two commands the other way round.
module TerminalQueueDrain =

    /// Why a terminal's head entry is not running (Plan 13, stage 2e). Distinguished because
    /// they resolve differently and a queue that says only *pending* leaves both looking like
    /// a stall: "waiting for approval" ends when a person makes a decision, "nick is using
    /// this terminal" ends when a person finishes a task. The agent's tool reports them
    /// apart, and so does the composer.
    type TerminalHold =
        /// The head needs an approval it has not got.
        | AwaitingApproval
        /// A peer holds the terminal's stdin. Resolves on release, and any peer may steal.
        | AwaitingTerminal
        /// A block is already running here. One at a time, per terminal.
        | AwaitingBlock
        /// The terminal is closed, or has nothing queued.
        | NotWaiting

    type TerminalDrainPlan =
        { /// The entries to start now — at most one per terminal, in terminal order.
          Ready : TerminalQueued list
          /// Entries a peer has refused: appended as `TerminalCommandRejected`, then
          /// removed. Separate from `Removals` because they are opposite facts — a removal
          /// is repair for something that already happened, a rejection is a decision that
          /// has not been recorded yet.
          Rejections : TerminalQueued list
          /// Doc keys to remove without running: entries a `TerminalBlockStarted` already
          /// names (a crash between the append and the removal), repaired rather than run
          /// a second time.
          Removals : QueueId list }

    /// The gate for ONE terminal, shared by `plan` and `holdOf` so what runs and what the
    /// queue says about why it is not running cannot drift apart.
    ///
    /// The order is the design: a refusal outranks every policy (it touches no pty, so a busy
    /// or leased terminal is irrelevant to it, and someone can clear a bad queue while a
    /// colleague is inside vim); then one block at a time; then the lease; then approval.
    let private gate
        (alreadyConsumed: TerminalQueued -> bool)
        (rejected: Set<string>)
        (busy: Set<string>)
        (leased: Set<string>)
        (isOpen: TerminalId -> bool)
        (modeOf: TerminalApprovalMode)
        (queue: Map<QueueId, TerminalQueued>)
        (terminal: TerminalId)
        : Choice<TerminalQueued, TerminalHold> =
        // The head is the first entry this drain has not already consumed. A refused entry is
        // not skipped over: it is still the head and it stops the queue exactly as an
        // unapproved one does, until the drain removes it. Running the entry behind it first
        // would reorder execution because of a verdict, which is the thing the approval gate
        // promises not to do.
        match TerminalQueueOrder.sortedFor terminal queue |> List.filter (alreadyConsumed >> not) |> List.tryHead with
        | None -> Choice2Of2 NotWaiting
        | Some entry when Set.contains (QueueId.value entry.QueueId) rejected -> Choice2Of2 NotWaiting
        | Some entry ->
            if not (isOpen terminal) then Choice2Of2 NotWaiting
            elif Set.contains (TerminalId.value terminal) busy then Choice2Of2 AwaitingBlock
            // Narrower than "the terminal is in live mode", and the narrowing is the point. A
            // block that BECAME live — the drain ran `vim`, alt-screen flipped the terminal —
            // is already held by `busy`. The gap this closes is a terminal live with NO block
            // running: someone pressed "take terminal" and is typing at the shell.
            elif Set.contains (TerminalId.value terminal) leased then Choice2Of2 AwaitingTerminal
            elif TerminalApprovalMode.requiresApproval modeOf entry.Author && Option.isNone entry.ApprovedBy then
                Choice2Of2 AwaitingApproval
            else Choice1Of2 entry

    /// Why a terminal's head is not running — `None` when it IS about to run. Pure, and asked
    /// by the agent's tool and the composer alike so neither invents its own answer.
    let holdOf
        (consumed: Set<string>)
        (busy: Set<string>)
        (leased: Set<string>)
        (isOpen: TerminalId -> bool)
        (modeOf: TerminalId -> TerminalApprovalMode)
        (queue: Map<QueueId, TerminalQueued>)
        (terminal: TerminalId)
        : TerminalHold option =
        let alreadyConsumed (entry: TerminalQueued) = Set.contains (QueueId.value entry.QueueId) consumed
        let rejected =
            queue
            |> Map.toList
            |> List.map snd
            |> List.filter (fun entry -> Option.isSome entry.RejectedBy && not (alreadyConsumed entry))
            |> List.map (fun entry -> QueueId.value entry.QueueId)
            |> Set.ofList
        match gate alreadyConsumed rejected busy leased isOpen (modeOf terminal) queue terminal with
        | Choice1Of2 _ -> None
        | Choice2Of2 hold -> Some hold

    /// `consumed` is the log-anchored exactly-once set; `busy` names terminals with a
    /// block already running; `leased` those a peer is typing into; `isOpen` and `modeOf`
    /// answer for the terminal an entry names. Nothing here reads the clock or the doc — it
    /// is given a snapshot and returns a decision.
    let plan
        (consumed: Set<string>)
        (busy: Set<string>)
        (leased: Set<string>)
        (isOpen: TerminalId -> bool)
        (modeOf: TerminalId -> TerminalApprovalMode)
        (queue: Map<QueueId, TerminalQueued>)
        : TerminalDrainPlan =

        let alreadyConsumed (entry: TerminalQueued) = Set.contains (QueueId.value entry.QueueId) consumed

        let terminals =
            queue
            |> Map.toList
            |> List.map (fun (_, entry) -> entry.Terminal)
            |> List.distinct
            |> List.sortBy TerminalId.value

        // A refusal outranks every other gate. It touches no terminal, so it does not wait
        // on `busy` or on the terminal being open, and it is checked before the mode gate
        // because a policy that would have auto-run the command must not beat a person who
        // said no. Under `AutoRun` that is the whole difference between the two.
        let rejections =
            queue
            |> Map.toList
            |> List.map snd
            |> List.filter (fun entry -> Option.isSome entry.RejectedBy && not (alreadyConsumed entry))
            |> List.sortBy (fun entry -> QueueId.value entry.QueueId)

        let rejected =
            rejections |> List.map (fun entry -> QueueId.value entry.QueueId) |> Set.ofList

        let ready =
            terminals
            |> List.choose (fun terminal ->
                match gate alreadyConsumed rejected busy leased isOpen (modeOf terminal) queue terminal with
                | Choice1Of2 entry -> Some entry
                | Choice2Of2 _ -> None)

        { Ready = ready
          Rejections = rejections
          Removals = queue |> Map.toList |> List.map snd |> List.filter alreadyConsumed |> List.map (fun e -> e.QueueId) }

    /// The consumed-set contribution of one event: a terminal drain dedups against every
    /// `TerminalBlockStarted` that names a queue entry — anchored in the log, never in the
    /// doc, so a replica that never saw the removal cannot re-run the command.
    /// A rejection joins the same set, and that is what settles the reject/drain race with
    /// no lock anywhere. Under `AutoRun` a human can press reject in the very tick the
    /// drain takes the entry; whichever event reaches the append-only log first wins and
    /// the second is dropped as already consumed. The Session Process is the log's only
    /// writer, so check-and-append is serial there by construction. A rejected `QueueId`
    /// can therefore never run afterwards, and a started one can never be retro-rejected —
    /// stopping something already running is `kill`, a different verb.
    let consumedOf (event: SessionEvent) : string option =
        match event with
        | SessionEvent.TerminalBlockStarted b -> b.QueueId |> Option.map QueueId.value
        | SessionEvent.TerminalCommandRejected r -> Some (QueueId.value r.QueueId)
        | _ -> None

/// How long `execute_command` waits, and for what (Plan 13, stage 3b).
///
/// The reason "queue and return" looked forced is that it answered one question — how do we
/// avoid blocking a turn on a human? — by giving up a different thing: waiting for a PROCESS,
/// which was never the problem. They are separate waits with separate bounds, and separating
/// them is the whole design:
///
///   * **Waiting for a person is unbounded in principle.** They may be asleep. So an approval
///     gets a short grace — enough that a supervised session, where approvals come in seconds,
///     still chains normally — and then the tool returns a handle. The turn continues.
///   * **Waiting for a process is already bounded**, by the same command timeout the old
///     `execute_command` used. Keeping it is not a new risk, it is the existing one.
///
/// Under `AutoRun` the first wait does not exist: the drain subscribes to the doc, the agent's
/// write is local to the Session Process, and the entry drains on the update. `AutoRun` is
/// therefore synchronous — met by having no network hop in the path rather than by hiding one.
module TerminalCommandWait =

    /// What the Process can see about one queued command right now.
    type Observation =
        { /// The block this request started, once it has. Joined to the request by the
          /// `QueueId` the block events carry.
          Block : TerminalBlock option
          /// Whether the request is still sitting in the doc's queue.
          InQueue : bool
          /// Whether it is the terminal's HEAD — the only entry a drain can start. An entry
          /// behind another is waiting for the queue, whatever the head happens to be waiting
          /// for, and reporting the head's reason as ours would tell the agent its own command
          /// needs an approval when somebody else's does.
          IsHead : bool
          /// Why the terminal's head is held, from `TerminalQueueDrain.holdOf`. `None` = it is
          /// about to run.
          Hold : TerminalQueueDrain.TerminalHold option }

    type Step =
        | Return of TerminalCommandStatus
        | KeepWaiting
        /// The request is in neither the queue nor the log: a peer deleted it before it ran.
        /// Deleting a queued entry is WITHDRAWAL, which has no event — so this is an absence,
        /// and reporting it as any outcome would be inventing one.
        | Gone

    /// The decision, given what has been observed and which deadlines have passed.
    ///
    /// `graceElapsed` bounds waiting on a PERSON; `deadlineElapsed` bounds waiting on a
    /// PROCESS. Which one applies is decided by what is actually being waited for, and that is
    /// the only interesting thing here.
    let step (graceElapsed: bool) (deadlineElapsed: bool) (observation: Observation) : Step =
        match observation.Block with
        | Some block ->
            match block.Status with
            | BlockRejected (by, reason) -> Return (TerminalCommandRefused (by, reason))
            | BlockFinished result -> Return (TerminalCommandRan result)
            // Still running. A deadline here is a YIELD, not a cancellation — the block runs
            // on, its output lands in the transcript, and the handle resumes it.
            | BlockRunning -> if deadlineElapsed then Return TerminalCommandRunning else KeepWaiting
        | None when not observation.InQueue -> Gone
        // Behind another entry: a wait on the QUEUE, bounded by the process deadline, because
        // what has to happen first is that the entries ahead run.
        | None when not observation.IsHead ->
            if deadlineElapsed then Return TerminalCommandAwaitingTerminal else KeepWaiting
        | None ->
            match observation.Hold with
            // A peer holding the terminal is mid-task and will not be done in five seconds, so
            // the grace does not apply — burning it on a wait that was never going to resolve
            // only makes the turn slower. Return at once.
            | Some TerminalQueueDrain.AwaitingTerminal -> Return TerminalCommandAwaitingTerminal
            | Some TerminalQueueDrain.AwaitingApproval ->
                if graceElapsed then Return TerminalCommandAwaitingApproval else KeepWaiting
            // Another block is running here: a wait on a PROCESS, so it gets the process
            // deadline rather than the approval grace — a quick command ahead of ours still
            // chains. Reported as `AwaitingTerminal` because from the caller's side both holds
            // say the same thing: the terminal is not free.
            | Some TerminalQueueDrain.AwaitingBlock ->
                if deadlineElapsed then Return TerminalCommandAwaitingTerminal else KeepWaiting
            // Nothing is holding it — it is about to run, or has just been consumed and the
            // block event has not landed yet. Bounded by the process deadline, because what is
            // being waited for is the run.
            | Some TerminalQueueDrain.NotWaiting
            | None -> if deadlineElapsed then Return TerminalCommandRunning else KeepWaiting

/// Who holds each terminal's stdin (Plan 13, stage 2e), as a pure transition over a map.
///
/// Pure rather than a mutable corner of the terminal manager because every interesting
/// question about leases is about which EVENTS a transition writes — a steal is two of them,
/// a release of a lease you do not hold is none — and those are exactly what an audit is
/// read from. The manager holds the map and calls these.
module TerminalLeases =

    /// Terminal id -> its holder, and whether detection gave it to them rather than them
    /// asking. The flag never leaves the process: it decides only whether the alt-screen flip
    /// may take the lease back, and "the program exited" is not a durable fact about a person.
    type Leases = Map<string, ActorRef * bool>

    let empty : Leases = Map.empty

    let holderOf (id: TerminalId) (leases: Leases) : ActorRef option =
        leases |> Map.tryFind (TerminalId.value id) |> Option.map fst

    /// Terminals someone is typing into — the drain's `leased` set, the exact counterpart of
    /// its `busy` one.
    let held (leases: Leases) : Set<string> = leases |> Map.toList |> List.map fst |> Set.ofList

    /// Claim the terminal, stealing it if someone else has it. Always succeeds: collaborators
    /// are trusted, so a steal needs no permission — what it needs is to be on the record,
    /// which is the pair of events this returns. The old lease ENDS BEFORE the new one starts,
    /// so a reader folding them in order never sees two holders.
    ///
    /// Re-taking a lease you already hold writes nothing. It is not a steal from yourself, and
    /// a log that said so would be noise the second time a client sent the frame.
    let take (id: TerminalId) (by: ActorRef) (auto: bool) (leases: Leases) : SessionEvent list * Leases =
        let key = TerminalId.value id
        match Map.tryFind key leases with
        | Some (holder, _) when holder = by -> [], leases
        | previous ->
            let ending =
                match previous with
                | Some (holder, _) ->
                    [ SessionEvent.TerminalLeaseReleased
                        { TerminalId = id; Was = holder; Reason = LeaseStolen by } ]
                | None -> []
            ending @ [ SessionEvent.TerminalLeaseTaken { TerminalId = id; By = by } ],
            Map.add key (by, auto) leases

    /// Hand the terminal back. Only the holder can: releasing someone else's lease is a steal
    /// wearing a polite word, and a steal is `take`, which says so on the record.
    let release (id: TerminalId) (by: ActorRef) (leases: Leases) : SessionEvent list * Leases =
        let key = TerminalId.value id
        match Map.tryFind key leases with
        | Some (holder, _) when holder = by ->
            [ SessionEvent.TerminalLeaseReleased { TerminalId = id; Was = holder; Reason = LeaseReleased } ],
            Map.remove key leases
        | _ -> [], leases

    /// Detection's release: the TUI exited and the lease was detection's to give back. Recorded
    /// as an ordinary release, because from outside the process that is what it is — the holder
    /// is done with the terminal — and a fourth reason for "the program they were in exited"
    /// would distinguish something no reader of the log is asking.
    let autoRelease (id: TerminalId) (leases: Leases) : SessionEvent list * Leases =
        match Map.tryFind (TerminalId.value id) leases with
        | Some (holder, true) ->
            [ SessionEvent.TerminalLeaseReleased { TerminalId = id; Was = holder; Reason = LeaseReleased } ],
            Map.remove (TerminalId.value id) leases
        | _ -> [], leases

    /// A peer's connection dropped: every lease it held ends. Without this a crashed tab
    /// leaves the composer reading "nick is using this terminal" for ever, with the queue held
    /// behind a peer who cannot release it — a deadlock wearing a status message's face.
    let peerGone (peer: PeerId) (leases: Leases) : SessionEvent list * Leases =
        let mine =
            leases
            |> Map.toList
            |> List.filter (fun (_, (holder, _)) -> holder = PeerRef peer)
            |> List.sortBy fst
        let events =
            mine
            |> List.choose (fun (key, (holder, _)) ->
                match TerminalId.create key with
                | Ok id ->
                    Some (
                        SessionEvent.TerminalLeaseReleased
                            { TerminalId = id; Was = holder; Reason = LeaseHolderGone })
                | Error _ -> None)
        events, mine |> List.fold (fun acc (key, _) -> Map.remove key acc) leases

/// The session's terminals: opening and closing them, and running one queued command at a
/// time in each. Owns no queue and no policy — the drain decides what runs, this runs it
/// and records what happened.
module SessionTerminals =

    /// How a command line becomes a process. A terminal composer holds a LINE (`ls -la |
    /// wc -l`), not an argv, so something has to interpret it, and that something is a
    /// shell. Configurable because the sandbox decides what shell exists inside it.
    type TerminalShell =
        { Executable : string
          /// Arguments before the command line itself.
          Arguments : string list
          /// Which instrumentation dialect this shell speaks — the key `TerminalMarks.rcFor`
          /// is asked for. A name we do not know means no instrumentation, and therefore a
          /// terminal that keeps the per-block path.
          Name : string
          /// How to launch this shell INTERACTIVELY, with its own startup files suppressed
          /// so ours is the only instrumentation in play.
          InteractiveArguments : string list }

    module TerminalShell =
        let posix : TerminalShell =
            { Executable = "/bin/sh"
              Arguments = [ "-c" ]
              Name = "sh"
              InteractiveArguments = [ "-i" ] }

        let bash : TerminalShell =
            { Executable = "/bin/bash"
              Arguments = [ "-c" ]
              Name = "bash"
              InteractiveArguments = [ "--noprofile"; "--norc"; "-i" ] }

    /// What the manager holds per live terminal (Plan 13, stage 2d).
    ///
    /// `Shell = None` is the DEGRADED terminal, and it is a complete answer rather than a
    /// broken one: blocks run as stage 1 ran them, one piped `Spawn` each. What it gives up
    /// is exactly what needs a shared shell — state carried between blocks, and a tty for a
    /// foreground program — so both are stateable at open rather than discovered later.
    ///
    /// Mutable, because the pty is spawned AFTER the terminal is in the live map: its output
    /// callback looks the terminal up by id, so the terminal has to exist before the first
    /// byte can arrive.
    type LiveTerminal =
        { Transcript : Transcript
          OpenedAt : DateTimeOffset
          Emulator : Emulator
          mutable Shell : PtyHandle option }

    /// Bytes of output one block may write to the transcript. Beyond it, output is dropped
    /// and the drop is RECORDED (`TerminalTranscriptTruncated`) — a runaway `yes` must not
    /// fill a disk, and an audit trail with a hole in it must say so rather than read as a
    /// complete record of a command that printed less than it did.
    let private blockOutputCap = 4 * 1024 * 1024

    /// A command line, as bytes to type into a shell's line editor. Three things, and none of
    /// them is cosmetic.
    ///
    /// Kill-line FIRST (`^U`), because the editor may already hold a half-typed line — a peer
    /// mid-keystroke in live mode, or a previous write that was never submitted.
    ///
    /// Control characters are STRIPPED, newline excepted. A terminal composer is
    /// collaborative and the agent writes into one, so command text is attacker-reachable in
    /// a way a locally typed line is not: an `ESC` in it could emit a forged mark from the
    /// INPUT side, where no nonce check can help because we are the one writing.
    ///
    /// Newline survives and becomes `\r`, because a heredoc IS a multi-line command and the
    /// shell reads its body from the lines that follow.
    let internal writeFor (command: string) : string =
        let kept =
            command
            |> Seq.filter (fun c -> c = '\n' || not (System.Char.IsControl c))
            |> Seq.map (fun c -> if c = '\n' then '\r' else c)
            |> Seq.toArray
            |> System.String
        "\u0015" + kept + "\r" 

    type SessionTerminals =
        { /// Open a terminal, ensuring the WorkSandbox exists first — opening one IS a
          /// need, so a session where nobody opens a terminal still starts nothing.
          Open : ActorRef -> string -> Async<Result<TerminalId, string>>
          /// Close a terminal. Rejected when it is not open.
          Close : TerminalId -> string -> Async<Result<unit, string>>
          /// Run one drained queue entry to completion, recording the block and streaming
          /// its output into the transcript. `onStarted` fires once the block's durable
          /// start event is written — that is the moment the queue entry has been consumed
          /// and its doc key may be removed, which is why it is a callback and not
          /// something the caller can do before or after the whole run.
          RunBlock : TerminalQueued -> string -> (unit -> unit) -> Async<unit>
          /// Record a peer's refusal of a queued command, minting the `BlockId` that names
          /// it. `onRecorded` fires once the event is durable — the moment the entry has
          /// been consumed and its doc key may go, exactly as `RunBlock`'s `onStarted`
          /// marks that moment for a command which ran.
          ///
          /// Needs no terminal: refusing a command touches no process, so it works on a
          /// terminal that is busy, leased (stage 2e) or closed. Someone can clear a bad
          /// queue while a colleague is inside vim.
          Reject : TerminalQueued -> string -> (unit -> unit) -> Async<unit>
          /// Take the terminal's stdin (Plan 13, stage 2e), stealing it from whoever holds it.
          /// Refused only when there is nothing to hold: a terminal that is not open, or a
          /// degraded one with no persistent shell to type into.
          Take : TerminalId -> ActorRef -> Async<Result<unit, string>>
          /// Hand it back. Refused when this actor is not the holder.
          Release : TerminalId -> ActorRef -> Async<Result<unit, string>>
          /// A peer's connection dropped: every lease it held ends, and the queues behind
          /// them are re-armed.
          PeerGone : PeerId -> Async<unit>
          /// Relay keystrokes to the pty. `false` when they were DROPPED — this actor does not
          /// hold the lease, or the terminal has no shell. Returned rather than logged here so
          /// the frame router logs it once, where it knows which peer sent it.
          ///
          /// Keystrokes are never written to the transcript. The shell echoes what is typed at
          /// it, so ordinary typing is already captured as output; what recording these would
          /// add is exactly what the terminal deliberately did not display — a password at an
          /// `ssh` prompt. See "Durable capture" in the plan.
          Input : TerminalId -> ActorRef -> string -> bool
          /// The lease holder's viewport size, applied to the pty and the emulator. `false`
          /// when dropped, on the same terms as `Input`.
          Resize : TerminalId -> ActorRef -> int -> int -> bool
          /// The synced size register, applied in BLOCK mode. Ignored while the terminal is
          /// leased: there, the holder's own viewport is what their foreground program has to
          /// agree with, and a bystander resizing the register must not redraw under them.
          /// Idempotent, so the doc watcher can call it on every update.
          ApplySize : TerminalId -> TerminalSize -> unit
          /// Terminals with a block running — the drain's `busy` set.
          Busy : unit -> Set<string>
          /// Terminals a peer is typing into — the drain's `leased` set, `busy`'s counterpart.
          Leased : unit -> Set<string>
          IsOpen : TerminalId -> bool
          /// Every open terminal's id and current transcript length, for a joining peer's
          /// catch-up hints.
          Lengths : unit -> (TerminalId * int) list
          /// Close every terminal left open by a previous process, at boot. A terminal is
          /// a live process in a sandbox that died with its session, so an event log that
          /// still says "open" is describing something that no longer exists.
          /// The current screen of an open terminal, with the transcript position it
          /// represents — what a joining peer is sent instead of replaying every byte
          /// (Plan 13, stage 2b). `None` for a terminal that is not open here.
          ///
          /// The seq is what makes the snapshot composable with the live feed: a client
          /// renders this, then folds records ABOVE that seq, exactly as it does with an
          /// event-offset catch-up.
          Snapshot : TerminalId -> Async<(int * string) option>
          ReconcileAtBoot : unit -> Async<unit> }

    /// A session with no terminals: every operation refuses, nothing is ever open.
    let unavailable : SessionTerminals =
        { Open = fun _ _ -> async { return Error "this session has no environment" }
          Close = fun _ _ -> async { return Error "this session has no terminals" }
          RunBlock = fun _ _ _ -> async { return () }
          Reject = fun _ _ _ -> async { return () }
          Take = fun _ _ -> async { return Error "this session has no terminals" }
          Release = fun _ _ -> async { return Error "this session has no terminals" }
          PeerGone = fun _ -> async { return () }
          Input = fun _ _ _ -> false
          Resize = fun _ _ _ _ -> false
          ApplySize = fun _ _ -> ()
          Busy = fun () -> Set.empty
          Leased = fun () -> Set.empty
          IsOpen = fun _ -> false
          Lengths = fun () -> []
          Snapshot = fun _ -> async { return None }
          ReconcileAtBoot = fun () -> async { return () } }

    /// Create the terminal manager for one session.
    ///
    /// `openTerminals` seeds the set left open by a previous process (folded from the
    /// durable log at boot) so `ReconcileAtBoot` can close them; `onRecord` broadcasts a
    /// record after it is durable; `actorFor` resolves a peer to its attribution, exactly
    /// as the message scheduler does.
    let create
        (log: EventLog<SessionEvent>)
        (environment: SessionEnvironment.SessionEnvironment)
        (openTranscript: OpenTranscript)
        (openEmulator: OpenEmulator)
        (shell: TerminalShell)
        (clock: unit -> DateTimeOffset)
        (mintTerminalId: unit -> TerminalId)
        (mintBlockId: unit -> BlockId)
        // The per-terminal mark nonce (Plan 13, stage 2d). Injected like every other mint so
        // tests are deterministic; the Host supplies a cryptographically random one, because
        // a guessable nonce is no nonce at all — the whole point is that output nobody
        // trusted cannot forge a block's outcome.
        (mintNonce: unit -> string)
        (onRecord: TerminalId -> int -> TranscriptRecord -> unit)
        // Re-arm the terminal drain (Plan 13, stage 2e). A lease ending frees a terminal
        // exactly as a block finishing does, so whatever queued behind it must start now —
        // and unlike block completion, a lease can end from inside here (the alt-screen flip,
        // a dropped peer) where the scheduler has nothing to observe.
        (reDrain: unit -> unit)
        (openAtBoot: TerminalId list)
        : SessionTerminals =

        let live = Collections.Generic.Dictionary<string, LiveTerminal> ()

        /// The block a pty terminal is waiting on: what to call when its `D` mark lands, and
        /// the per-block output accounting the piped path keeps in locals. Keyed by terminal,
        /// because a terminal runs one block at a time — which is the same invariant `busy`
        /// already states.
        let pending = Collections.Generic.Dictionary<string, (int -> unit) * (unit -> int) * (int -> unit)> ()
        /// Who wrote the block currently running in each terminal — the input the flip policy
        /// needs, since "a TUI took the screen" only says whose keyboard it should be if we
        /// know whose command started it.
        let runningAuthor = Collections.Generic.Dictionary<string, ActorRef> ()
        /// The size last applied to each pty, so the block-mode register watcher can run on
        /// every doc update without resizing a terminal that has not changed size.
        let appliedSize = Collections.Generic.Dictionary<string, TerminalSize> ()
        let mutable busy : Set<string> = Set.empty
        let mutable leases = TerminalLeases.empty
        let mutable leftOpen : Set<string> = openAtBoot |> List.map TerminalId.value |> Set.ofList

        let append event =
            async {
                let! _ = log.Append ActorRef.SessionProcess event
                return ()
            }

        let appendAs actor event =
            async {
                let! _ = log.Append actor event
                return ()
            }

        let isOpen (id: TerminalId) =
            live.ContainsKey (TerminalId.value id) || Set.contains (TerminalId.value id) leftOpen

        /// Write a record and tell the peers. Durable first, visible second.
        ///
        /// The emulator is fed here and only here, which is what makes the screen a pure
        /// function of the transcript: every byte that reaches one reaches the other, in the
        /// same order, so folding the transcript through a fresh emulator reproduces this
        /// one. That property is what the join snapshot rests on, and it is pinned by a test.
        let emit (id: TerminalId) (terminal: LiveTerminal) (kind: TranscriptKind) (data: string) =
            let transcript = terminal.Transcript
            let emulator = terminal.Emulator
            let record =
                { At = (clock () - terminal.OpenedAt).TotalSeconds
                  Kind = kind
                  Data = data }
            let seq = transcript.Append record
            // Only what the terminal PRINTED shapes the screen. An input record is what we
            // wrote to the process, not what came back; feeding it here would draw the
            // command twice on a pty, which echoes it itself.
            match kind with
            | TranscriptOutput | TranscriptStderr -> emulator.Write data
            | TranscriptInput | TranscriptResize -> ()
            onRecord id seq record

        /// Who a lease event is attributed to. A take and a steal are the acts of whoever took
        /// it; a voluntary release is the holder's; a lease ended because its holder vanished
        /// is nobody's but the Process's, which is the honest reading of `LeaseHolderGone`.
        let leaseActor (event: SessionEvent) =
            match event with
            | SessionEvent.TerminalLeaseTaken t -> t.By
            | SessionEvent.TerminalLeaseReleased r ->
                match r.Reason with
                | LeaseReleased -> r.Was
                | LeaseStolen by -> by
                | LeaseHolderGone -> ActorRef.SessionProcess
            | _ -> ActorRef.SessionProcess

        /// Commit a lease transition: the map first, then its events, then the re-arm.
        ///
        /// The map moves before the appends so that a drain woken by them already sees the new
        /// holder — the whole point of the gate is that a terminal handed back is available to
        /// the queue, and one just taken is not.
        let applyLease (events: SessionEvent list, next: TerminalLeases.Leases) : Async<unit> =
            async {
                if List.isEmpty events then return ()
                else
                    leases <- next
                    for event in events do
                        do! appendAs (leaseActor event) event
                    reDrain ()
            }

        /// What the alternate screen proposes, applied. Detection never overrides a lease
        /// somebody asked for, and only ever gives back what detection itself took —
        /// `TerminalFlip.propose` is where both rules live.
        let flip (id: TerminalId) (altScreen: bool) : Async<unit> =
            async {
                let key = TerminalId.value id
                let holder = TerminalLeases.holderOf id leases
                let autoHeld = leases |> Map.tryFind key |> Option.map snd |> Option.defaultValue false
                let author =
                    match runningAuthor.TryGetValue key with
                    | true, actor -> Some actor
                    | _ -> None
                match TerminalFlip.propose altScreen holder autoHeld author with
                | FlipToLive by -> do! applyLease (TerminalLeases.take id by true leases)
                | FlipToBlock -> do! applyLease (TerminalLeases.autoRelease id leases)
                | FlipNothing -> return ()
            }

        /// Open the terminal's ONE instrumented shell, and report whether it took.
        ///
        /// Probed by RUNNING it: the shell is launched with our rc file and we wait, bounded,
        /// for its first prompt mark. Arrived, the terminal is instrumented. Absent — an image
        /// whose shell we cannot instrument, or which ignores the mechanism — the pty is torn
        /// down and the terminal keeps the per-block path. Deciding by observation rather than
        /// by shell name is the point: a POSIX `sh` has no prompt hook at all and rides its
        /// marks in PS1, which is the shakiest of the three and cannot be trusted on a name.
        let openShell (id: TerminalId) (terminal: LiveTerminal) : Async<unit> =
            async {
                let key = TerminalId.value id
                let nonce = mintNonce ()
                match TerminalMarks.rcFor shell.Name nonce with
                | None -> return ()
                | Some rc ->
                    let carry = ref ""
                    let ready = ref false
                    let onOutput (data: string) =
                        match live.TryGetValue key with
                        | false, _ -> ()
                        | true, current ->
                            let marks, clean, rest = TerminalMarks.scan nonce carry.Value data
                            carry.Value <- rest
                            for m in marks do
                                match m with
                                | MarkPromptStart -> ready.Value <- true
                                // A second `C` inside an open block is dropped rather than
                                // repaired: the Process knows what it wrote, so a surprise is
                                // evidence about the shell, never something to act on.
                                | MarkCommandStart -> ()
                                | MarkCommandDone code ->
                                    match pending.TryGetValue key with
                                    | true, (complete, _, _) ->
                                        pending.Remove key |> ignore
                                        complete code
                                    // A `D` with no block open is the shell's own prompt
                                    // cycle — at startup, or after a peer typed something in
                                    // live mode. Not ours to act on.
                                    | _ -> ()
                            // Nothing before the first prompt mark reaches the transcript.
                            // What arrives then is the shell's own startup and the echo of
                            // the bootstrap we just typed — it belongs to no block, and
                            // recording it would put our instrumentation in the audit trail.
                            if clean <> "" && ready.Value then
                                // Per-block output accounting lives with the pending block,
                                // because on a pty the stream belongs to the TERMINAL and the
                                // cap is a property of the block.
                                match pending.TryGetValue key with
                                | true, (_, written, note) ->
                                    let room = blockOutputCap - written ()
                                    if room <= 0 then note clean.Length
                                    else
                                        let kept = if clean.Length <= room then clean else clean.Substring (0, room)
                                        note (clean.Length - kept.Length)
                                        emit id current TranscriptOutput kept
                                | _ -> emit id current TranscriptOutput clean
                    let! spawned =
                        environment.SpawnPty
                            { Executable = shell.Executable
                              Arguments = shell.InteractiveArguments
                              Env = Map.empty
                              WorkingDirectory = None }
                            80
                            24
                            onOutput
                    match spawned with
                    | Error _ -> return ()
                    | Ok pty ->
                        terminal.Shell <- Some pty
                        // TYPED into the shell rather than written to a file it is launched
                        // with. No temp file to place inside a sandbox this Process cannot
                        // reach, no second spawn to set one up, and it works for any shell
                        // that has a prompt — which is Warp's approach for the same reasons.
                        // Every line starts with a space, so the shell's own
                        // ignore-duplicates-and-space history setting keeps our bootstrap out
                        // of the user's history.
                        for line in rc.Split '\n' do
                            pty.Write (line + "\r")
                        // Poll rather than race: this is the open path, not a hot one, and a
                        // 50ms granularity beats carrying cancellation machinery Fable would
                        // have to reproduce.
                        let rec await (remaining: int) =
                            async {
                                if ready.Value then return true
                                elif remaining <= 0 then return false
                                else
                                    do! Async.Sleep 50
                                    return! await (remaining - 50)
                            }
                        let! instrumented = await 3000
                        if not instrumented then
                            // Uninstrumented. Tear the shell down and keep the per-block
                            // path, which answers a smaller question completely.
                            pty.Kill ()
                            terminal.Shell <- None
            }

        let openTerminal (openedBy: ActorRef) (title: string) : Async<Result<TerminalId, string>> =
            async {
                // A terminal is a need, and the need is identified before the terminal
                // exists — so a failed environment start is reported as a failed open
                // rather than as a terminal nothing can run in.
                match! environment.Ensure None "a terminal was opened" with
                | EnvironmentUnavailable reason -> return Error reason
                | EnvironmentAvailable ->
                    let id = mintTerminalId ()
                    let openedAt = clock ()
                    let transcript =
                        openTranscript
                            id
                            { Width = 80
                              Height = 24
                              Timestamp = openedAt.ToUnixTimeSeconds () }
                    let terminal =
                        { Transcript = transcript
                          OpenedAt = openedAt
                          Emulator = openEmulator 80 24
                          Shell = None }
                    // In the live map BEFORE the shell starts: the pty's output callback finds
                    // the terminal by id, and bytes can arrive the instant it spawns.
                    live.[TerminalId.value id] <- terminal
                    appliedSize.[TerminalId.value id] <- TerminalSize.default'
                    terminal.Emulator.OnAltScreen (fun alt -> Async.StartImmediate (flip id alt))
                    do! openShell id terminal
                    do! appendAs openedBy (SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = openedBy; Title = title })
                    return Ok id
            }

        let closeTerminal (id: TerminalId) (reason: string) : Async<Result<unit, string>> =
            async {
                if not (isOpen id) then return Error "terminal is not open"
                else
                    // The emulator is a live JS object; a closed terminal keeps its blocks
                    // (the audit outlives the process) but not its screen.
                    match live.TryGetValue (TerminalId.value id) with
                    | true, terminal ->
                        terminal.Emulator.Dispose ()
                        terminal.Shell |> Option.iter (fun pty -> pty.Kill ())
                    | _ -> ()
                    pending.Remove (TerminalId.value id) |> ignore
                    runningAuthor.Remove (TerminalId.value id) |> ignore
                    appliedSize.Remove (TerminalId.value id) |> ignore
                    live.Remove (TerminalId.value id) |> ignore
                    leftOpen <- Set.remove (TerminalId.value id) leftOpen
                    busy <- Set.remove (TerminalId.value id) busy
                    // The lease goes with the terminal, and WITHOUT an event: `TerminalClosed`
                    // already clears the holder in the projection, so appending a release
                    // beside it would be two mechanisms for one fact — free to disagree the
                    // moment one of them is missed.
                    leases <- Map.remove (TerminalId.value id) leases
                    do! append (SessionEvent.TerminalClosed { TerminalId = id; Reason = reason })
                    return Ok ()
            }

        let runBlock (entry: TerminalQueued) (command: string) (onStarted: unit -> unit) : Async<unit> =
            async {
                let key = TerminalId.value entry.Terminal
                match live.TryGetValue key with
                | false, _ ->
                    // The terminal closed between the plan and the run. The entry stays in
                    // the doc (nothing consumed it), and the next drain will find the
                    // terminal shut and leave it alone.
                    return ()
                | true, terminal ->
                    let transcript = terminal.Transcript
                    busy <- Set.add key busy
                    // The flip policy's input: if this command takes the alternate screen, its
                    // author is the person who now needs the keyboard.
                    runningAuthor.[key] <- entry.Author
                    let blockId = mintBlockId ()
                    // Taken BEFORE the command is written, which is forced by the anchor
                    // ordering below and is the honest reading anyway: on a pty the shell
                    // echoes the command itself, and that echo is part of what this block put
                    // on the screen. The alternative — starting the range at the `C` mark —
                    // cannot be reconciled with appending the anchor first, because `C` does
                    // not exist until after the write.
                    let fromSeq = transcript.NextSeq ()
                    // Durable BEFORE the process starts: the block event is the
                    // exactly-once anchor, so a crash between here and the spawn leaves a
                    // block that never completed — visible and explicable — rather than a
                    // command that silently runs twice.
                    do!
                        appendAs
                            entry.Author
                            (SessionEvent.TerminalBlockStarted
                                { TerminalId = entry.Terminal
                                  BlockId = blockId
                                  QueueId = Some entry.QueueId
                                  Author = entry.Author
                                  ApprovedBy = entry.ApprovedBy |> Option.map PeerRef
                                  Command = command
                                  FromSeq = fromSeq })
                    // Consumed: the durable fact exists, so the doc key can go. Between
                    // the append and this call the entry is in both places, which the
                    // drain answers by planning against the log-anchored `consumed` set
                    // rather than against the doc.
                    onStarted ()
                    // The command line is echoed into the transcript as INPUT, so a replay
                    // shows what was typed as well as what came back — the same reason
                    // asciinema records `"i"` events at all.
                    emit entry.Terminal terminal TranscriptInput (command + "\n")

                    let mutable written = 0
                    let mutable dropped = 0

                    let! result =
                        match terminal.Shell with
                        | Some pty ->
                            // TYPED into the terminal's one shell, which is what makes `cd`
                            // in this block move the next one — the property a per-block
                            // spawn structurally cannot have.
                            async {
                                let complete = Async.FromContinuations (fun (cont, _, _) ->
                                    pending.[key] <-
                                        ((fun code -> cont code),
                                         (fun () -> written),
                                         (fun extra ->
                                             dropped <- dropped + extra
                                             written <- written + extra)))
                                pty.Write (writeFor command)
                                let! code = complete
                                return (if code = 0 then CommandSucceeded 0 else CommandFailed code)
                            }
                        | None ->
                            // The degraded terminal: stage 1's path, unchanged.
                            async {
                                let onChunk (stream: OutputStream, text: string) =
                                    if written >= blockOutputCap then dropped <- dropped + text.Length
                                    else
                                        let room = blockOutputCap - written
                                        let kept = if text.Length <= room then text else text.Substring (0, room)
                                        dropped <- dropped + (text.Length - kept.Length)
                                        written <- written + kept.Length
                                        let kind = match stream with Stdout -> TranscriptOutput | Stderr -> TranscriptStderr
                                        emit entry.Terminal terminal kind kept
                                let! spawned =
                                    environment.Spawn
                                        { Executable = shell.Executable
                                          Arguments = shell.Arguments @ [ command ]
                                          Env = Map.empty
                                          WorkingDirectory = None }
                                        onChunk
                                match spawned with
                                | Error reason -> return CommandExecutionFailed reason
                                | Ok handle ->
                                    match! handle.Exited with
                                    | SandboxExited 0 -> return CommandSucceeded 0
                                    | SandboxExited code -> return CommandFailed code
                                    | SandboxRunFailed reason -> return CommandExecutionFailed reason
                            }
                    if dropped > 0 then
                        do!
                            append
                                (SessionEvent.TerminalTranscriptTruncated
                                    { TerminalId = entry.Terminal; BlockId = Some blockId; DroppedBytes = dropped })
                    do!
                        append
                            (SessionEvent.TerminalBlockCompleted
                                { TerminalId = entry.Terminal
                                  BlockId = blockId
                                  Result = result
                                  ToSeq = transcript.NextSeq () })
                    runningAuthor.Remove key |> ignore
                    busy <- Set.remove key busy
            }

        let take (id: TerminalId) (by: ActorRef) : Async<Result<unit, string>> =
            async {
                match live.TryGetValue (TerminalId.value id) with
                | false, _ -> return Error "terminal is not open"
                // A degraded terminal has no persistent shell, so there is no stdin to hold:
                // its blocks are separate processes that end with themselves. Refusing here is
                // the same declare-and-skip honesty the fallback is built on — better than a
                // lease that is granted and then does nothing.
                | true, terminal when Option.isNone terminal.Shell ->
                    return Error "this terminal has no interactive shell"
                | true, _ ->
                    do! applyLease (TerminalLeases.take id by false leases)
                    return Ok ()
            }

        let release (id: TerminalId) (by: ActorRef) : Async<Result<unit, string>> =
            async {
                match TerminalLeases.holderOf id leases with
                | Some holder when holder = by ->
                    do! applyLease (TerminalLeases.release id by leases)
                    return Ok ()
                | Some _ -> return Error "another peer holds this terminal"
                | None -> return Error "this terminal is not held"
            }

        let peerGone (peer: PeerId) : Async<unit> = applyLease (TerminalLeases.peerGone peer leases)

        /// The pty of a terminal this actor is entitled to type into, if any.
        let heldPty (id: TerminalId) (by: ActorRef) : PtyHandle option =
            match TerminalLeases.holderOf id leases with
            | Some holder when holder = by ->
                match live.TryGetValue (TerminalId.value id) with
                | true, terminal -> terminal.Shell
                | _ -> None
            | _ -> None

        let input (id: TerminalId) (by: ActorRef) (data: string) : bool =
            match heldPty id by with
            | Some pty ->
                pty.Write data
                true
            | None -> false

        let resize (id: TerminalId) (by: ActorRef) (cols: int) (rows: int) : bool =
            if not (TerminalSize.isValid { Cols = cols; Rows = rows }) then false
            else
                match heldPty id by with
                | Some pty ->
                    let key = TerminalId.value id
                    pty.Resize cols rows
                    match live.TryGetValue key with
                    | true, terminal ->
                        terminal.Emulator.Resize cols rows
                        // A resize IS output-side history, not a keystroke: asciicast records
                        // it (`[t, "r", "COLSxROWS"]`) because a replay that does not know the
                        // screen changed shape redraws everything after it wrongly.
                        emit id terminal TranscriptResize (sprintf "%dx%d" cols rows)
                    | _ -> ()
                    appliedSize.[key] <- { Cols = cols; Rows = rows }
                    true
                | None -> false

        let applySize (id: TerminalId) (size: TerminalSize) : unit =
            let key = TerminalId.value id
            let unchanged =
                match appliedSize.TryGetValue key with
                | true, current -> current = size
                | _ -> false
            // Leased terminals are the holder's to size, and an invalid size reaches us from a
            // doc any peer can write — neither is a resize.
            if unchanged || not (TerminalSize.isValid size) || Map.containsKey key leases then ()
            else
                match live.TryGetValue key with
                | true, terminal ->
                    terminal.Shell |> Option.iter (fun pty -> pty.Resize size.Cols size.Rows)
                    terminal.Emulator.Resize size.Cols size.Rows
                    emit id terminal TranscriptResize (sprintf "%dx%d" size.Cols size.Rows)
                    appliedSize.[key] <- size
                | _ -> ()

        let reject (entry: TerminalQueued) (command: string) (onRecorded: unit -> unit) : Async<unit> =
            async {
                match entry.RejectedBy with
                | None -> return ()
                | Some by ->
                    // Attributed to the peer who refused, exactly as an approval is: the
                    // doc holds the connection fact and the event carries the actor.
                    do!
                        appendAs
                            (PeerRef by)
                            (SessionEvent.TerminalCommandRejected
                                { TerminalId = entry.Terminal
                                  QueueId = entry.QueueId
                                  BlockId = mintBlockId ()
                                  Author = entry.Author
                                  RejectedBy = PeerRef by
                                  Command = command
                                  Reason = entry.RejectedReason })
                    onRecorded ()
            }

        let reconcileAtBoot () : Async<unit> =
            async {
                // Every terminal the log still calls open belongs to a process that is
                // gone. Closing them here is what keeps the projection honest, and it is
                // deliberately an APPEND rather than a rewrite: the terminal really was
                // open until this moment.
                for key in Set.toList leftOpen do
                    match TerminalId.create key with
                    | Ok id -> do! append (SessionEvent.TerminalClosed { TerminalId = id; Reason = "session restarted" })
                    | Error _ -> ()
                leftOpen <- Set.empty
            }

        { Open = openTerminal
          Close = closeTerminal
          RunBlock = runBlock
          Reject = reject
          Take = take
          Release = release
          PeerGone = peerGone
          Input = input
          Resize = resize
          ApplySize = applySize
          Busy = fun () -> busy
          Leased = fun () -> TerminalLeases.held leases
          IsOpen = isOpen
          Lengths =
            fun () ->
                live
                |> Seq.choose (fun kv ->
                    match TerminalId.create kv.Key with
                    | Ok id -> Some (id, kv.Value.Transcript.NextSeq ())
                    | Error _ -> None)
                |> List.ofSeq
          Snapshot =
            fun id ->
                async {
                match live.TryGetValue (TerminalId.value id) with
                | false, _ -> return None
                // Read the length FIRST, then serialize. The two are taken under no lock —
                // there is none to take — so the only ordering that cannot lie is the one
                // that reports a screen at least as new as the seq it claims: a client that
                // re-folds a record already drawn here is idempotent, while one that misses
                // a record drawn after the seq would never draw it at all.
                | true, terminal ->
                    // Seq FIRST, then the screen. Records written while the barrier drains
                    // land on the screen but not in the seq, so the snapshot is at worst
                    // NEWER than it claims — a client re-folds what it already drew, which
                    // is idempotent. The other order would report a seq for a record the
                    // screen has not drawn, and the client would skip it for ever.
                    let seq = terminal.Transcript.NextSeq ()
                    let! screen = terminal.Emulator.Serialize ()
                    return Some (seq, screen)
                }
          ReconcileAtBoot = reconcileAtBoot }

/// The terminal queue's consumer loop — the message scheduler's sibling, and deliberately
/// NOT the same object. An agent turn and a terminal command have no reason to wait for
/// one another: a build running in a terminal must not stop the agent from answering, and
/// a long agent turn must not stop a person from running `git status`. Two independent
/// consumers over two independent queues, triggered by the same doc updates.
module TerminalScheduler =

    type TerminalScheduler =
        { /// Re-examine the terminal queues now. Called on every doc update, and again
          /// whenever a block finishes (which is what lets a terminal's next queued
          /// command start immediately).
          Drain : unit -> unit }

    /// `initialConsumed` seeds the log-anchored exactly-once set from the durable log at
    /// boot — every `QueueId` a `TerminalBlockStarted` already names.
    let create
        (doc: Yjs.Y.Doc)
        (terminals: SessionTerminals.SessionTerminals)
        (initialConsumed: Set<string>)
        : TerminalScheduler =

        let mutable consumed = initialConsumed

        let rec drain () =
            match SyncedStateSync.ofDoc doc with
            | Error _ -> ()
            | Ok synced ->

            // The size register's block-mode path (Plan 13, stage 2e). `TerminalResize` is
            // live-mode only, so in block mode the Process is what tells the pty its size —
            // a shell that never learns its size is the one that redraws wrongly. Run before
            // the queue check rather than after it, because a terminal with nothing queued is
            // exactly the one whose size a peer is likely to be adjusting.
            for terminal, size in Map.toList synced.TerminalSizes do
                terminals.ApplySize terminal size

            if Map.isEmpty synced.TerminalQueue then () else

                let plan =
                    TerminalQueueDrain.plan
                        consumed
                        (terminals.Busy ())
                        (terminals.Leased ())
                        terminals.IsOpen
                        (fun terminal -> SyncedSessionState.modeOf terminal synced)
                        synced.TerminalQueue
                // Leftovers first: a crash between the start append and the doc removal
                // leaves an entry that is already a block, and repairing it is free.
                SyncedStateSync.removeTerminalQueued doc plan.Removals
                // Refusals next, and before anything runs: they are what a person decided,
                // and they free the head of a queue that a `Ready` entry may be sitting
                // behind. Same snapshot-then-consume shape as a command that runs.
                for entry in plan.Rejections do
                    let command = SyncedStateSync.terminalQueuedText doc entry.QueueId
                    consumed <- Set.add (QueueId.value entry.QueueId) consumed
                    Async.StartImmediate (
                        async {
                            do!
                                terminals.Reject
                                    entry
                                    command
                                    (fun () -> SyncedStateSync.removeTerminalQueued doc [ entry.QueueId ])
                            // The head may now be a different entry, and it may be ready.
                            drain ()
                        })
                for entry in plan.Ready do
                    // Snapshot the command from THIS replica at the instant it is
                    // consumed, exactly as the message drain snapshots a body: what runs
                    // is what the queue said when it was taken, and later edits to a
                    // fragment nobody can reach change nothing.
                    let command = SyncedStateSync.terminalQueuedText doc entry.QueueId
                    consumed <- Set.add (QueueId.value entry.QueueId) consumed
                    Async.StartImmediate (
                        async {
                            do!
                                terminals.RunBlock
                                    entry
                                    command
                                    (fun () -> SyncedStateSync.removeTerminalQueued doc [ entry.QueueId ])
                            // The terminal is free again: whatever queued behind this
                            // command starts now.
                            drain ()
                        })

        { Drain = drain }

/// The agent's ONE execution path (Plan 13, stage 3b): queue a command where everyone can see
/// it, wait as long as it is worth waiting, and answer with what happened.
///
/// PR 1 shipped the terminal tool BESIDE `execute_command` rather than instead of it, and that
/// split was worse than either end of it: the gated path returned before anything happened
/// while the ungated one returned an answer, so a model that needed an answer took the path
/// that gave one. The consequence was not a nudge being ignored — it was that `ApproveAgent`
/// was UNENFORCEABLE, because the agent had a second door with no gate on it. The gate only
/// becomes real when there is one door, which is this.
module TerminalCommands =

    /// How long an approval is waited for. Short, because the point is only that a SUPERVISED
    /// session — where someone is watching and approvals come in seconds — still chains
    /// normally. Beyond it the tool yields a handle rather than holding the turn.
    let approvalGrace = TimeSpan.FromSeconds 5.0

    /// How long a RUNNING command is waited for: the same bound the retired `execute_command`
    /// used. Keeping it is not a new risk, it is the existing one.
    let commandTimeout = TimeSpan.FromSeconds 120.0

    /// Register a listener for "something that could change an outcome happened" — a log
    /// append, a doc update. Returns the unsubscribe.
    type OnChanged = (unit -> unit) -> (unit -> unit)

    type TerminalCommands =
        { Execute : ExecuteCommand
          Read : ReadTerminalBlock }

    let unavailable : TerminalCommands =
        { Execute = fun _ _ -> async { return Error "this session has no terminals" }
          Read = fun _ -> async { return Error "this session has no terminals" } }

    /// Wake on the next change, or on a short tick.
    ///
    /// The tick is a floor, not the mechanism: a change wakes the wait immediately, which is
    /// what makes `AutoRun` synchronous, and the tick only guarantees that a deadline can
    /// still fire in a quiet session where nothing else is happening.
    let private nextWake (onChanged: OnChanged) : Async<unit> =
        Async.FromContinuations (fun (cont, _, _) ->
            let mutable fired = false
            let mutable unsubscribe : unit -> unit = ignore
            let go () =
                if not fired then
                    fired <- true
                    unsubscribe ()
                    cont ()
            unsubscribe <- onChanged go
            Async.StartImmediate (
                async {
                    do! Async.Sleep 100
                    go ()
                }))

    /// `projection` and `syncedOf` are read fresh on every observation — the whole loop is a
    /// fold over what the Process already knows, holding no state of its own, so a restart
    /// mid-wait loses a wait and never an outcome.
    let create
        (doc: Yjs.Y.Doc)
        (terminals: SessionTerminals.SessionTerminals)
        (projection: unit -> TerminalProjection)
        (syncedOf: unit -> Result<SyncedSessionState, Ylmish.Codec.Error list>)
        (readOutput: TerminalId -> int -> int option -> string)
        /// The session's agent terminal, opened on first use with `reason` as its TITLE — so
        /// the strip says "running the test suite" rather than "agent", which is what the
        /// retired `ensure_environment`'s one genuinely useful argument becomes.
        (agentTerminal: string -> Async<Result<TerminalId, string>>)
        (mintQueueId: unit -> QueueId)
        (now: unit -> DateTimeOffset)
        (onChanged: OnChanged)
        : TerminalCommands =

        /// Every block in the session, keyed by the request it came from. The join between a
        /// handle and its outcome, and the reason `TerminalBlock` carries its `QueueId`.
        let blockFor (handle: QueueId) : (TerminalId * TerminalBlock) option =
            (projection ()).Terminals
            |> List.tryPick (fun terminal ->
                terminal.Blocks
                |> List.tryFind (fun block -> block.QueueId = Some handle)
                |> Option.map (fun block -> terminal.TerminalId, block))

        /// The exactly-once set, derived from the projection rather than borrowed from the
        /// scheduler: a block or a refusal that names a `QueueId` IS that entry having been
        /// consumed, and the projection already holds both.
        let consumed () =
            (projection ()).Terminals
            |> List.collect (fun t -> t.Blocks)
            |> List.choose (fun b -> b.QueueId |> Option.map QueueId.value)
            |> Set.ofList

        let observe (terminal: TerminalId) (handle: QueueId) : TerminalCommandWait.Observation =
            let block = blockFor handle |> Option.map snd
            match syncedOf () with
            | Error _ ->
                // A doc that will not decode says nothing about this request. Keep waiting on
                // whatever the log shows rather than declaring the entry gone.
                { Block = block; InQueue = true; IsHead = false; Hold = None }
            | Ok synced ->
                let consumed = consumed ()
                let head =
                    TerminalQueueOrder.sortedFor terminal synced.TerminalQueue
                    |> List.filter (fun e -> not (Set.contains (QueueId.value e.QueueId) consumed))
                    |> List.tryHead
                { Block = block
                  InQueue = Map.containsKey handle synced.TerminalQueue
                  IsHead = (head |> Option.map (fun e -> e.QueueId)) = Some handle
                  Hold =
                    TerminalQueueDrain.holdOf
                        consumed
                        (terminals.Busy ())
                        (terminals.Leased ())
                        terminals.IsOpen
                        (fun t -> SyncedSessionState.modeOf t synced)
                        synced.TerminalQueue
                        terminal }

        let outcomeOf (terminal: TerminalId) (handle: QueueId) (status: TerminalCommandStatus) =
            let block = blockFor handle |> Option.map snd
            let output =
                match block with
                | Some b -> readOutput terminal b.FromSeq b.ToSeq
                | None -> ""
            let elided = max 0 (output.Length - TerminalDigest.tailCap)
            { Terminal = terminal
              Handle = handle
              Block = block |> Option.map (fun b -> b.BlockId)
              Status = status
              OutputTail = (if elided > 0 then output.Substring elided else output)
              Elided = elided }

        /// Wait out both phases, then answer. Both deadlines are measured from `startedAt`
        /// rather than from entry into a phase, so a command that spends its grace waiting for
        /// an approval and then runs still gets the full command timeout it would have had.
        let rec awaitOutcome (terminal: TerminalId) (handle: QueueId) (startedAt: DateTimeOffset) (runningSince: DateTimeOffset option) =
            async {
                let elapsedSince (from: DateTimeOffset) = now () - from
                let observation = observe terminal handle
                // The process deadline runs from the moment the block STARTED, when one has;
                // before that there is no process to time.
                let deadlineFrom = runningSince |> Option.defaultValue startedAt
                let graceElapsed = elapsedSince startedAt >= approvalGrace
                let deadlineElapsed = elapsedSince deadlineFrom >= commandTimeout
                match TerminalCommandWait.step graceElapsed deadlineElapsed observation with
                | TerminalCommandWait.Return status -> return Ok (outcomeOf terminal handle status)
                | TerminalCommandWait.Gone ->
                    return Error "that command was removed from the queue before it ran"
                | TerminalCommandWait.KeepWaiting ->
                    let runningSince =
                        match runningSince, observation.Block with
                        | None, Some block when block.Status = BlockRunning -> Some (now ())
                        | existing, _ -> existing
                    do! nextWake onChanged
                    return! awaitOutcome terminal handle startedAt runningSince
            }

        let execute (requested: TerminalId option) (command: string) : Async<Result<TerminalCommandOutcome, string>> =
            async {
                let command = command.Trim ()
                if command = "" then return Error "a command cannot be empty"
                else
                    let! terminal =
                        async {
                            match requested with
                            | Some id when terminals.IsOpen id -> return Ok id
                            | Some _ -> return Error "that terminal is not open"
                            // The agent's own terminal, titled with what it is for.
                            | None -> return! agentTerminal command
                        }
                    match terminal, syncedOf () with
                    | Error reason, _ -> return Error reason
                    | Ok _, Error _ -> return Error "the session's collaborative state could not be read"
                    | Ok terminal, Ok synced ->
                        let handle = mintQueueId ()
                        // Visible to every peer the instant this lands — before any waiting —
                        // because the point of the one door is that what the agent is about to
                        // run is something people can read, edit and refuse.
                        SyncedStateSync.enqueueTerminalCommand
                            doc
                            handle
                            terminal
                            ActorRef.Agent
                            (TerminalQueueOrder.nextFor terminal synced.TerminalQueue)
                            command
                        return! awaitOutcome terminal handle (now ()) None
            }

        let read (handle: QueueId) : Async<Result<TerminalCommandOutcome, string>> =
            async {
                // The handle names a request, so it is resolvable from either side: the block
                // it became, or the entry it still is.
                let terminal =
                    match blockFor handle with
                    | Some (terminal, _) -> Some terminal
                    | None ->
                        match syncedOf () with
                        | Ok synced -> synced.TerminalQueue |> Map.tryFind handle |> Option.map (fun e -> e.Terminal)
                        | Error _ -> None
                match terminal with
                | None -> return Error "no such command"
                // Resumed under the same two-phase policy, which is what makes a late approval
                // chainable: the caller waits again rather than being told "still waiting" for
                // ever in a loop of its own.
                | Some terminal -> return! awaitOutcome terminal handle (now ()) None
            }

        { Execute = execute; Read = read }
