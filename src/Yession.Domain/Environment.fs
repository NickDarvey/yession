namespace Yession.Domain

open System

/// Environment & command vocabulary (Steps 11–13). Capabilities are pre-scoped to a
/// session — none of them accepts a `SessionId` — and container handles are validated
/// by the Session Manager on every use, so a fabricated or cross-session handle never
/// reaches a container (docs/design.md §3, §5 "Capabilities are scoped, not ambient").

type EnvironmentKind =
    | Docker
    /// Commands run as plain local child processes of the Manager — the engine that is
    /// verifiable in every environment. The authority contract is engine-independent;
    /// the Docker adapter is exercised where a daemon exists.
    | LocalProcess

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

type EnvironmentSpec =
    { Kind : EnvironmentKind
      WorkingDirectory : string option
      Image : ContainerImage option
      Build : ContainerBuildSpec option
      Mounts : ContainerMount list
      EnvironmentVariables : Map<string, EnvironmentVariableRef> }

module EnvironmentSpec =

    /// The minimal built-in spec: local child processes, session defaults everywhere.
    let localProcess : EnvironmentSpec =
        { Kind = LocalProcess
          WorkingDirectory = None
          Image = None
          Build = None
          Mounts = []
          EnvironmentVariables = Map.empty }

    /// A one-line description for lifecycle events.
    let summary (spec: EnvironmentSpec) : string =
        match spec.Kind, spec.Image with
        | Docker, Some image -> sprintf "docker:%s%s" image.Name (image.Tag |> Option.map ((+) ":") |> Option.defaultValue "")
        | Docker, None -> "docker"
        | LocalProcess, _ -> "local-process"

/// A Manager-validated container handle. The fields are private — code outside this
/// module reads them only through the accessors and can never rebind them — and the
/// Manager checks existence + session ownership on every use, so constructing a handle
/// grants nothing.
type ContainerHandle =
    private { HandleSessionId : SessionId; HandleContainerId : string }

module ContainerHandle =
    let create (sessionId: SessionId) (containerId: string) : ContainerHandle =
        { HandleSessionId = sessionId; HandleContainerId = containerId }
    let sessionId (handle: ContainerHandle) = handle.HandleSessionId
    let containerId (handle: ContainerHandle) = handle.HandleContainerId

type StartContainerResult =
    | ContainerStarted of ContainerHandle
    | ContainerStartFailed of reason: string

type StopContainerResult =
    | ContainerStopped
    | ContainerStopFailed of reason: string

// --- Commands (Step 13 shapes; the capability surface needs them from Step 11).
// `CommandId` lives in Identity.fs and `OutputStream`/`CommandResult` in Events.fs
// (the lifecycle is recorded as events). ----------------------------------------------

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

// --- The session-scoped capability surface (no SessionId parameters) -----------------

type StartSessionContainer = EnvironmentSpec -> Async<StartContainerResult>
type StopSessionContainer = ContainerHandle -> Async<StopContainerResult>

/// Output streams through the chunk callback in order; the async resolves with the
/// command's final result (mirroring `RunAgent`'s streaming shape).
type ExecuteInSessionContainer =
    ContainerHandle -> CommandRequest -> (CommandOutputChunk -> unit) -> Async<CommandResult>

/// Everything a Session Process may do to environments — handed to it at launch,
/// already scoped to its session. There is no other environment authority.
type SessionEnvironmentCapabilities =
    { StartContainer : StartSessionContainer
      StopContainer : StopSessionContainer
      Execute : ExecuteInSessionContainer }

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
