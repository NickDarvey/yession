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
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
      /// The sandbox's own process — compose's `command` (Plan 27).
      ///
      /// `None` is what every sandbox has had until now: the container idles and exists
      /// only to be `exec`'d into. `Some` makes it a service, and inherits the semantics
      /// that go with one — the sandbox lives exactly as long as the process does, so a
      /// command that exits takes its terminals with it. That is docker's own behaviour
      /// and not a policy invented here.
      ///
      /// DOCKER ONLY. A container has a `Cmd`; a host or srt sandbox is a confinement
      /// around spawns, with no process of its own to replace, so asking for one there is
      /// refused rather than ignored (`Sandboxes.forBackend`).
      Command : string option }

module EnvironmentSpec =

    /// The minimal built-in spec: session defaults everywhere.
    let defaults : EnvironmentSpec =
        { WorkingDirectory = None
          Image = None
          Build = None
          Mounts = []
          EnvironmentVariables = Map.empty
          Command = None }

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
