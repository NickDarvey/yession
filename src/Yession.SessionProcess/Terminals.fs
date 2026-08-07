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

    /// `consumed` is the log-anchored exactly-once set; `busy` names terminals with a
    /// block already running; `isOpen` and `modeOf` answer for the terminal an entry
    /// names. Nothing here reads the clock or the doc — it is given a snapshot and returns
    /// a decision.
    let plan
        (consumed: Set<string>)
        (busy: Set<string>)
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
                if not (isOpen terminal) || Set.contains (TerminalId.value terminal) busy then None
                else
                    // The head is the first entry this drain has not already consumed. A
                    // refused entry is not skipped over: it is still the head, and it stops
                    // the queue exactly as an unapproved one does until the drain removes
                    // it. Running the entry behind it first would reorder execution because
                    // of a verdict, which is the thing the approval gate promises not to do.
                    TerminalQueueOrder.sortedFor terminal queue
                    |> List.filter (alreadyConsumed >> not)
                    |> List.tryHead
                    |> Option.filter (fun entry ->
                        not (Set.contains (QueueId.value entry.QueueId) rejected)
                        && (not (TerminalApprovalMode.requiresApproval (modeOf terminal) entry.Author)
                            || Option.isSome entry.ApprovedBy)))

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
          Arguments : string list }

    module TerminalShell =
        let posix : TerminalShell = { Executable = "/bin/sh"; Arguments = [ "-c" ] }

    /// Bytes of output one block may write to the transcript. Beyond it, output is dropped
    /// and the drop is RECORDED (`TerminalTranscriptTruncated`) — a runaway `yes` must not
    /// fill a disk, and an audit trail with a hole in it must say so rather than read as a
    /// complete record of a command that printed less than it did.
    let private blockOutputCap = 4 * 1024 * 1024

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
          /// Terminals with a block running — the drain's `busy` set.
          Busy : unit -> Set<string>
          IsOpen : TerminalId -> bool
          /// Every open terminal's id and current transcript length, for a joining peer's
          /// catch-up hints.
          Lengths : unit -> (TerminalId * int) list
          /// Close every terminal left open by a previous process, at boot. A terminal is
          /// a live process in a sandbox that died with its session, so an event log that
          /// still says "open" is describing something that no longer exists.
          ReconcileAtBoot : unit -> Async<unit> }

    /// A session with no terminals: every operation refuses, nothing is ever open.
    let unavailable : SessionTerminals =
        { Open = fun _ _ -> async { return Error "this session has no environment" }
          Close = fun _ _ -> async { return Error "this session has no terminals" }
          RunBlock = fun _ _ _ -> async { return () }
          Reject = fun _ _ _ -> async { return () }
          Busy = fun () -> Set.empty
          IsOpen = fun _ -> false
          Lengths = fun () -> []
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
        (shell: TerminalShell)
        (clock: unit -> DateTimeOffset)
        (mintTerminalId: unit -> TerminalId)
        (mintBlockId: unit -> BlockId)
        (onRecord: TerminalId -> int -> TranscriptRecord -> unit)
        (openAtBoot: TerminalId list)
        : SessionTerminals =

        /// What the manager holds per live terminal: its transcript, the instant every
        /// record's `At` is relative to, and whether a block is running in it.
        let live = Collections.Generic.Dictionary<string, Transcript * DateTimeOffset> ()
        let mutable busy : Set<string> = Set.empty
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
        let emit (id: TerminalId) (transcript: Transcript) (openedAt: DateTimeOffset) (kind: TranscriptKind) (data: string) =
            let record =
                { At = (clock () - openedAt).TotalSeconds
                  Kind = kind
                  Data = data }
            let seq = transcript.Append record
            onRecord id seq record

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
                    live.[TerminalId.value id] <- (transcript, openedAt)
                    do! appendAs openedBy (SessionEvent.TerminalOpened { TerminalId = id; OpenedBy = openedBy; Title = title })
                    return Ok id
            }

        let closeTerminal (id: TerminalId) (reason: string) : Async<Result<unit, string>> =
            async {
                if not (isOpen id) then return Error "terminal is not open"
                else
                    live.Remove (TerminalId.value id) |> ignore
                    leftOpen <- Set.remove (TerminalId.value id) leftOpen
                    busy <- Set.remove (TerminalId.value id) busy
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
                | true, (transcript, openedAt) ->
                    busy <- Set.add key busy
                    let blockId = mintBlockId ()
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
                    emit entry.Terminal transcript openedAt TranscriptInput (command + "\n")

                    let mutable written = 0
                    let mutable dropped = 0
                    let onChunk (stream: OutputStream, text: string) =
                        if written >= blockOutputCap then dropped <- dropped + text.Length
                        else
                            let room = blockOutputCap - written
                            let kept = if text.Length <= room then text else text.Substring (0, room)
                            dropped <- dropped + (text.Length - kept.Length)
                            written <- written + kept.Length
                            let kind = match stream with Stdout -> TranscriptOutput | Stderr -> TranscriptStderr
                            emit entry.Terminal transcript openedAt kind kept

                    let! spawned =
                        environment.Spawn
                            { Executable = shell.Executable
                              Arguments = shell.Arguments @ [ command ]
                              Env = Map.empty
                              WorkingDirectory = None }
                            onChunk
                    let! result =
                        async {
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
                    busy <- Set.remove key busy
            }

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
          Busy = fun () -> busy
          IsOpen = isOpen
          Lengths =
            fun () ->
                live
                |> Seq.choose (fun kv ->
                    match TerminalId.create kv.Key with
                    | Ok id -> Some (id, (fst kv.Value).NextSeq ())
                    | Error _ -> None)
                |> List.ofSeq
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
            | Ok synced when Map.isEmpty synced.TerminalQueue -> ()
            | Ok synced ->
                let plan =
                    TerminalQueueDrain.plan
                        consumed
                        (terminals.Busy ())
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
