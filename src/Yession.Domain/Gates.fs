namespace Yession.Domain

/// The approval gate, in the abstract (Plan 15, stage 3).
///
/// Plan 13 built this for terminals: a command waits in collaborative state until a human
/// says yes, and the mode that decides whether it has to wait is a synced register anyone
/// can change. Nothing in that arrangement is about terminals — the terminal-shaped parts
/// are the serial drain and the editable command line, and both live elsewhere. What is
/// left is here, and it is what every gated command shares.
///
/// The evidence the generalization is real rather than forced: the mode below needs no new
/// case to serve both sides.

/// Who must approve an act before it happens. A synced register per subject (collaborative
/// state, so changing it is visible to everyone immediately), read by whatever will carry
/// the act out — the terminal drain, or the command gate.
type ApprovalMode =
    /// Anything proposed happens. The terminal a person opened for themselves; the commands
    /// nobody has asked to review.
    | AutoRun
    /// A human's act happens; the agent's waits for one. The reason the terminal composer
    /// looks like the message composer: reviewing what the agent is about to run is the same
    /// act as reading what it is about to say.
    | ApproveAgent
    /// Everything waits for an explicit approval, including a human's own.
    | ApproveAll

module ApprovalMode =

    let describe =
        function
        | AutoRun -> "auto"
        | ApproveAgent -> "approve-agent"
        | ApproveAll -> "approve-all"

    let parse (raw: string) : ApprovalMode option =
        match raw with
        | "auto" -> Some AutoRun
        | "approve-agent" -> Some ApproveAgent
        | "approve-all" -> Some ApproveAll
        | _ -> None

    /// Whether an act proposed by `author` needs an approval under this mode. Pure, and
    /// the single place the policy is stated — the drain asks it, the command gate asks it,
    /// and so does the UI that decides whether to show an approve button, so none of the
    /// three can disagree.
    let requiresApproval (mode: ApprovalMode) (author: ActorRef) : bool =
        match mode with
        | AutoRun -> false
        | ApproveAll -> true
        | ApproveAgent ->
            match author with
            | Agent -> true
            // A human's act, or the Process's own: no gate. `System`/`SessionProcess` acts
            // are not agent-authored, and gating them would deadlock a drain waiting for a
            // human to approve the runtime's own housekeeping.
            | UserRef _ | PeerRef _ | SessionProcess | System -> false

/// What a gate is ABOUT: the thing a mode is configured for, and the key that mode is
/// stored under.
type GateSubject =
    /// One terminal. Long-lived, so its mode is something a person sets once and reads
    /// off the terminal's own header.
    | ForTerminal of TerminalId
    /// One command, by its MCP tool name. Not by capability or by category: the name is
    /// what the human sees in the proposal and what the model called, so a mode set against
    /// it means exactly what it looks like it means.
    | ForCommand of string

module GateSubject =

    let describe =
        function
        | ForTerminal id -> "terminal:" + TerminalId.value id
        | ForCommand name -> "command:" + name

    let parse (raw: string) : GateSubject option =
        if raw.StartsWith "terminal:" then
            match TerminalId.create (raw.Substring 9) with
            | Ok id -> Some (ForTerminal id)
            | Error _ -> None
        elif raw.StartsWith "command:" then
            match raw.Substring 8 with
            | "" -> None
            | name -> Some (ForCommand name)
        else None

    /// The mode a subject has when nobody has configured one. Per KIND, and that is how
    /// today's behaviour survives on both sides of the generalization: a terminal is
    /// reviewed unless someone says otherwise, a command is not reviewed until someone
    /// says so. Stating it as a default rather than as a stored register also means a
    /// subject nobody has touched carries nothing that could go stale.
    let defaultMode =
        function
        | ForTerminal _ -> ApproveAgent
        | ForCommand _ -> AutoRun

/// One command a gate can be put on.
///
/// A terminal announces itself — it is in the projection, so every surface can see the list.
/// A command has no such existence: it is a function somebody wrote, and nothing about it
/// reaches a screen unless it is written down. That is what this is, and it is in the SHARED
/// domain rather than behind a declaration protocol because the browser compiles against
/// this assembly: the list is a value both sides already have, and inventing a transport to
/// send a build-time constant across a wire would be work in exchange for a way to disagree.
type GatedCommand =
    { /// The MCP tool name: what the model calls, what the register is keyed by, and what a
      /// refusal records.
      Tool : string
      /// What a human should see on the control. Prose, not the tool name — the surface is
      /// for the person deciding whether they want to be asked.
      Title : string }

/// Every command with a gate on it. The single source of the set, which is what makes the
/// two things that need it — the settings control and the boot configuration — impossible to
/// disagree about.
module GatedCommands =

    let addRepo = { Tool = "add_repo"; Title = "adding a repo" }
    let switchBranch = { Tool = "switch_branch"; Title = "switching a repo's branch" }
    let startWorkSandbox = { Tool = "start_work_sandbox"; Title = "starting a work sandbox" }
    let stopWorkSandbox = { Tool = "stop_work_sandbox"; Title = "stopping a work sandbox" }

    /// In the order a human reads them, which is the order they render in.
    let all : GatedCommand list = [ addRepo; switchBranch; startWorkSandbox; stopWorkSandbox ]

    let tryFind (tool: string) : GatedCommand option =
        all |> List.tryFind (fun command -> command.Tool = tool)

    /// The subject a command's mode is stored under.
    let subject (command: GatedCommand) : GateSubject = ForCommand command.Tool
