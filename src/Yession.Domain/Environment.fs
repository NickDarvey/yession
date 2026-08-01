namespace Yession.Domain

open System

/// Environment & command vocabulary. The session's WorkSandbox is described by an
/// `EnvironmentSpec` and confined by whichever `SandboxBackend` the session was booted
/// with; the lifecycle is recorded as events (the folds below), which are the pinned
/// observable protocol.

type ContainerImage = { Name : string; Tag : string option }

type ContainerBuildSpec = { ContextPath : string; DockerfilePath : string option }

type MountSource =
    | HostPath of string
    | NamedVolume of string
    | SessionWorkspace

type MountMode =
    | ReadOnly
    | ReadWrite

type ContainerMount = { Source : MountSource; Target : string; Mode : MountMode }

type SecretName = private SecretName of string

module SecretName =
    let create (raw: string) : Result<SecretName, string> =
        if String.IsNullOrWhiteSpace raw then Error "SecretName cannot be blank"
        else Ok (SecretName (raw.Trim()))
    let value (SecretName s) = s

type EnvironmentVariableRef =
    | PlainValue of string
    | SecretRef of SecretName

/// What the session's WorkSandbox looks like, independent of the backend that confines
/// it. Image/build/mounts apply to the docker backend; environment variables (with
/// `SecretRef`s resolved at sandbox spawn) and the working directory apply everywhere.
type EnvironmentSpec =
    { WorkingDirectory : string option
      Image : ContainerImage option
      Build : ContainerBuildSpec option
      Mounts : ContainerMount list
      EnvironmentVariables : Map<string, EnvironmentVariableRef> }

module EnvironmentSpec =

    /// The minimal built-in spec: session defaults everywhere.
    let defaults : EnvironmentSpec =
        { WorkingDirectory = None
          Image = None
          Build = None
          Mounts = []
          EnvironmentVariables = Map.empty }

// --- Commands (Step 13 shapes). `CommandId` lives in Identity.fs and
// `OutputStream`/`CommandResult` in Events.fs (the lifecycle is recorded as events). ---

type CommandRequest =
    { CommandId : CommandId
      Executable : string
      Arguments : string list
      WorkingDirectory : string option
      Environment : Map<string, string>
      Timeout : TimeSpan option }

type CommandOutputChunk =
    { CommandId : CommandId
      Stream : OutputStream
      Text : string }

// --- Environment UI state, projected from events (Step 12) ---------------------------

type EnvironmentStatus =
    | EnvironmentNotStarted
    | EnvironmentStarting
    | EnvironmentRunning of containerRef: string
    | EnvironmentFailed of reason: string
    | EnvironmentDown

module EnvironmentStatus =

    /// Fold one event into the environment's UI status. Deterministic: the status is a
    /// pure function of the ordered event sequence.
    let applyEvent (status: EnvironmentStatus) (event: SessionEvent) : EnvironmentStatus =
        match event with
        | SessionEvent.EnvironmentStartRequested _ -> EnvironmentStarting
        | SessionEvent.EnvironmentStarted e -> EnvironmentRunning e.ContainerRef
        | SessionEvent.EnvironmentStartFailed e -> EnvironmentFailed e.Reason
        | SessionEvent.EnvironmentStopped _ -> EnvironmentDown
        | _ -> status

// --- The read-only command log, projected from events (Step 13) ----------------------

type CommandLogStatus =
    | CommandPending
    | CommandRunning
    | CommandFinished of CommandResult

type CommandLogEntry =
    { CommandId : CommandId
      Executable : string
      Arguments : string list
      Status : CommandLogStatus
      /// Output chunks in arrival order (per-command ordering is a pinned contract).
      Output : (OutputStream * string) list }

type CommandLog = { Entries : CommandLogEntry list }

module CommandLog =

    let empty : CommandLog = { Entries = [] }

    let private updateEntry (commandId: CommandId) (f: CommandLogEntry -> CommandLogEntry) (log: CommandLog) =
        { Entries = log.Entries |> List.map (fun e -> if e.CommandId = commandId then f e else e) }

    /// Fold one event into the command log. Deterministic and read-only by construction:
    /// there is no other way to produce a log entry.
    let applyEvent (log: CommandLog) (event: SessionEvent) : CommandLog =
        match event with
        | SessionEvent.CommandRequested c ->
            { Entries =
                log.Entries
                @ [ { CommandId = c.CommandId
                      Executable = c.Executable
                      Arguments = c.Arguments
                      Status = CommandPending
                      Output = [] } ] }
        | SessionEvent.CommandStarted c ->
            log |> updateEntry c.CommandId (fun e -> { e with Status = CommandRunning })
        | SessionEvent.CommandOutputReceived c ->
            log |> updateEntry c.CommandId (fun e -> { e with Output = e.Output @ [ c.Stream, c.Text ] })
        | SessionEvent.CommandCompleted c ->
            log |> updateEntry c.CommandId (fun e -> { e with Status = CommandFinished c.Result })
        | _ -> log
