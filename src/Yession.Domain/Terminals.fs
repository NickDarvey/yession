namespace Yession.Domain

/// Terminals, projected from the event log (docs/plans/12). Like the conversation, this
/// is a pure fold over ordered events and nothing else — never the doc, never live
/// output. That is what makes the terminal list, its blocks, and their exit codes
/// identical on every replica and after every reload, and it is why there is no way for a
/// client to invent a block: the only constructor is an event the Session Process wrote.
///
/// The bytes a block printed are NOT here. They live in the terminal's transcript
/// (`Transcript.fs`); a block records the transcript range it produced, and a renderer
/// joins the two. The split is the whole design: facts fold, bytes stream.

/// Who must approve a command before it runs. A synced register per terminal (collaborative
/// state, so changing it is visible to everyone immediately), read by the Session Process's
/// drain — which is the only thing that can act on it.
type TerminalApprovalMode =
    /// Anything queued runs. The terminal a person opened for themselves.
    | AutoRun
    /// A human's command runs; the agent's waits for one. The default, and the reason the
    /// composer looks like the message composer: reviewing what the agent is about to run
    /// is the same act as reading what it is about to say.
    | ApproveAgent
    /// Everything waits for an explicit approval, including a human's own.
    | ApproveAll

module TerminalApprovalMode =

    let describe =
        function
        | AutoRun -> "auto"
        | ApproveAgent -> "approve-agent"
        | ApproveAll -> "approve-all"

    let parse (raw: string) : TerminalApprovalMode option =
        match raw with
        | "auto" -> Some AutoRun
        | "approve-agent" -> Some ApproveAgent
        | "approve-all" -> Some ApproveAll
        | _ -> None

    /// Whether a command written by `author` needs an approval under this mode. Pure, and
    /// the single place the policy is stated — the drain asks it, and so does the UI that
    /// decides whether to show an approve button, so the two cannot disagree.
    let requiresApproval (mode: TerminalApprovalMode) (author: ActorRef) : bool =
        match mode with
        | AutoRun -> false
        | ApproveAll -> true
        | ApproveAgent ->
            match author with
            | Agent -> true
            // A human's command, or the Process's own: no gate. `System`/`SessionProcess`
            // commands are not agent-authored, and gating them would deadlock a drain
            // waiting for a human to approve the runtime's own housekeeping.
            | UserRef _ | PeerRef _ | SessionProcess | System -> false

/// Where a block is in its life.
type TerminalBlockStatus =
    | BlockRunning
    | BlockFinished of CommandResult

/// One executed command and the transcript range it produced.
type TerminalBlock =
    { BlockId : BlockId
      /// Who wrote the command.
      Author : ActorRef
      /// Who approved it, when the mode required one.
      ApprovedBy : ActorRef option
      Command : string
      /// First transcript line of this block's output.
      FromSeq : int
      /// One past its last transcript line; `None` while it is still running.
      ToSeq : int option
      Status : TerminalBlockStatus }

/// One terminal, as the UI knows it.
type TerminalView =
    { TerminalId : TerminalId
      Title : string
      OpenedBy : ActorRef
      /// A closed terminal keeps its blocks: the audit outlives the process.
      IsOpen : bool
      /// Why it closed, when it has.
      ClosedReason : string option
      /// Blocks in the order they ran.
      Blocks : TerminalBlock list
      /// Output this terminal produced that the transcript did not keep. Non-zero means
      /// the record has a stated gap.
      DroppedBytes : int }

/// Every terminal this session has had, in the order they were opened.
type TerminalProjection = { Terminals : TerminalView list }

module TerminalProjection =

    let empty : TerminalProjection = { Terminals = [] }

    let private updateTerminal (id: TerminalId) (f: TerminalView -> TerminalView) (proj: TerminalProjection) =
        { Terminals = proj.Terminals |> List.map (fun t -> if t.TerminalId = id then f t else t) }

    let private updateBlock (id: BlockId) (f: TerminalBlock -> TerminalBlock) (view: TerminalView) =
        { view with Blocks = view.Blocks |> List.map (fun b -> if b.BlockId = id then f b else b) }

    /// Fold one event into the projection. Only terminal events matter; everything else
    /// passes through, so this composes with the other folds over the same page.
    let applyEvent (proj: TerminalProjection) (event: SessionEvent) : TerminalProjection =
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
                          IsOpen = true
                          ClosedReason = None
                          Blocks = []
                          DroppedBytes = 0 } ] }
        | SessionEvent.TerminalClosed e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with IsOpen = false; ClosedReason = Some e.Reason })
        | SessionEvent.TerminalBlockStarted e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                if t.Blocks |> List.exists (fun b -> b.BlockId = e.BlockId) then t
                else
                    { t with
                        Blocks =
                            t.Blocks
                            @ [ { BlockId = e.BlockId
                                  Author = e.Author
                                  ApprovedBy = e.ApprovedBy
                                  Command = e.Command
                                  FromSeq = e.FromSeq
                                  ToSeq = None
                                  Status = BlockRunning } ] })
        | SessionEvent.TerminalBlockCompleted e ->
            proj
            |> updateTerminal e.TerminalId (fun t ->
                t |> updateBlock e.BlockId (fun b -> { b with ToSeq = Some e.ToSeq; Status = BlockFinished e.Result }))
        | SessionEvent.TerminalTranscriptTruncated e ->
            proj |> updateTerminal e.TerminalId (fun t -> { t with DroppedBytes = t.DroppedBytes + e.DroppedBytes })
        | _ -> proj

    let tryFind (id: TerminalId) (proj: TerminalProjection) : TerminalView option =
        proj.Terminals |> List.tryFind (fun t -> t.TerminalId = id)

    /// The terminals still open, in open order — what the panel lists.
    let openTerminals (proj: TerminalProjection) : TerminalView list =
        proj.Terminals |> List.filter (fun t -> t.IsOpen)

    /// The block currently running in a terminal, if any. At most one: the drain runs a
    /// terminal's queue one command at a time, which is what makes a shell's working
    /// directory and environment mean anything from one command to the next.
    let runningBlock (view: TerminalView) : TerminalBlock option =
        view.Blocks |> List.tryFind (fun b -> b.Status = BlockRunning)
