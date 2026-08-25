namespace Yession.Domain

/// What running a command produces: which stream a byte came from, and how the run ended.
///
/// Kernel vocabulary rather than any one feature's fact. The gate speaks it (the
/// `Command*` events are its lifecycle) and so does a terminal block, which is why it
/// cannot live in `Events.fs` any more: a feature's facts compile ABOVE that file, and
/// `TerminalBlockCompleted` names `CommandResult`.
type OutputStream =
    | Stdout
    | Stderr

type CommandResult =
    | CommandSucceeded of exitCode: int
    | CommandFailed of exitCode: int
    | CommandTimedOut
    | CommandExecutionFailed of reason: string
