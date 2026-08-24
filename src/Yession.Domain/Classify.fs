namespace Yession.Domain.Agent

open Yession.Domain

/// Classification: the gate every proposed act passes on its way to happening (Plan 23).
///
/// This replaces manual approval. The old arrangement parked an act in collaborative state
/// until a person pressed a button; this one asks a CLASSIFIER at the moment the act would
/// run, and the act runs or is refused on its answer. Same door, different doorkeeper — the
/// one-door property (`execute_command` is the agent's only execution path, and raw bytes
/// into a shell would be the way around it) is what survives of the approval gate, and it
/// is the part that was ever load-bearing.

/// One act, as it stands at the moment it would run — not as it was proposed. A queued
/// command line stays editable until the drain takes it, so what the classifier reads is
/// what will actually execute.
type ProposedAct =
    /// A shell command line about to run in a terminal.
    | TerminalAct of terminal: TerminalId * command: string
    /// A structured command: the tool, its encoded arguments, and the summary a person
    /// reads (`add_repo octo/hello`).
    | CommandAct of tool: string * args: string * summary: string

/// The classifier's answer. Its own DU rather than a reuse: `Decision` (authorization),
/// `Verdict` (the deleted manual gate) and `Policy` (sandbox, resilience) each already name
/// a different kind of answer in this codebase.
type Classification =
    | Approved
    /// Refused, with a reason the proposer is told — a refusal that reads as a malfunction
    /// gets retried another way.
    | Rejected of reason: string

/// Decide whether an act may happen, given who proposed it and what it now says. Async
/// because the intended implementation asks a model; the bypass below answers instantly.
type Classifier = ActorRef -> ProposedAct -> Async<Classification>

module Classifier =

    /// Approves everything. The shipped classifier: until an AI-driven one exists, nothing
    /// but the work sandbox stands between a proposed act and its execution — recorded as a
    /// gap in `docs/GAPS.md`, not a property to rely on.
    let approveAll : Classifier = fun _ _ -> async { return Approved }
