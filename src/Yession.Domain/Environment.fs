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

/// What a CONTAINER is. Every field here is one only a container has — an image to run, a
/// filesystem to build, volumes to mount, a process to be.
type ContainerSpec =
    { Image : ContainerImage option
      Build : ContainerBuildSpec option
      Mounts : ContainerMount list
      /// The container's own process — compose's `command` (Plan 27).
      ///
      /// `None` is what every sandbox has had until now: the container idles and exists only
      /// to be `exec`'d into. `Some` makes it a service and inherits the semantics that go
      /// with one — the sandbox lives exactly as long as the process does, so a command that
      /// exits takes its terminals with it. That is docker's own behaviour, not a policy
      /// invented here.
      Command : string option }

module ContainerSpec =

    let defaults : ContainerSpec =
        { Image = None; Build = None; Mounts = []; Command = None }

/// What the sandbox IS, and the distinction is load-bearing rather than descriptive.
///
/// This used to be four optional fields on one record, and every one of them was meaningless
/// on two of the three backends: a host or srt sandbox is a CONFINEMENT AROUND SPAWNS — it
/// has no image, no volumes and no process of its own — so `Command = Some _` on one was a
/// state the type permitted, the composition refused at run time, and a reader had to know
/// the rule to avoid writing.
///
/// As a union it is simply not expressible. "A confined sandbox with a process" has no
/// representation, so nothing downstream carries a case for it and no test has to prove it
/// is refused.
///
/// What remains a run-time question, honestly: whether the BACKEND can host a container at
/// all. The backend is the operator's (`YESSION_SESSION_WORK_BACKEND`) and the spec is partly
/// the repo's, so the two are authored by different people and can only be reconciled where
/// they meet (`Sandboxes.forBackend`).
type SandboxRuntime =
    /// host / srt. Nothing to build, nothing to run: the sandbox IS the confinement its
    /// spawns go through. (Not `Confined` — `FilesystemConfinement` already has that case,
    /// and a second one would shadow it wherever the domain is opened.)
    | Confinement
    /// docker.
    | Container of ContainerSpec

/// What the session's WorkSandbox looks like. The working directory and the environment
/// (with `SecretRef`s resolved at sandbox spawn) apply to every runtime; everything that is
/// a container's alone lives in `Container`.
type EnvironmentSpec =
    { WorkingDirectory : string option
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
      Runtime : SandboxRuntime }

module EnvironmentSpec =

    /// The minimal built-in spec: session defaults everywhere.
    let defaults : EnvironmentSpec =
        { WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Runtime = Confinement }

    /// The same, as a container — what the docker backend starts from when nothing asked for
    /// anything in particular.
    let container : EnvironmentSpec =
        { defaults with Runtime = Container ContainerSpec.defaults }

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
