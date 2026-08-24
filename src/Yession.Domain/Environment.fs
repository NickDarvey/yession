namespace Yession.Domain.Sandboxes

open Yession.Domain

open System

/// Environment & command vocabulary. The session's WorkSandbox is described by an
/// `EnvironmentSpec` and confined by whichever `SandboxBackend` the session was booted
/// with; the lifecycle is recorded as events (the folds below), which are the pinned
/// observable protocol.

type ContainerImage = { Name : string; Tag : string option }

module ContainerImage =

    /// `name:tag`, as docker spells it — one rendering, because two would eventually
    /// disagree about whether an untagged image says `:latest`.
    let render (image: ContainerImage) : string =
        image.Name + (image.Tag |> Option.map ((+) ":") |> Option.defaultValue "")

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

module SandboxRuntime =

    /// What a sandbox IS, in one clause: an image, a build or neither, plus the process if
    /// it has one. Deliberately shallow — it is read by a person deciding whether the thing
    /// running is the thing they asked for, in a refusal and in the sandbox listing.
    let describe (runtime: SandboxRuntime) : string =
        match runtime with
        | Confinement -> "no container"
        | Container container ->
            let what =
                match container.Image, container.Build with
                | Some image, _ -> ContainerImage.render image
                | None, Some build -> sprintf "a build of %s" build.ContextPath
                | None, None -> "a container"
            match container.Command with
            | Some command -> sprintf "%s running '%s'" what command
            | None -> what

/// What the session's WorkSandbox looks like. The working directory and the environment
/// (with `SecretRef`s resolved at sandbox spawn) apply to every runtime; everything that is
/// a container's alone lives in `Container`.
type EnvironmentSpec =
    { WorkingDirectory : string option
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
      /// Egress this sandbox ASKS to reach. `[]` is not "nothing" — it is the sandbox saying
      /// nothing, and what that means is the operator's answer (`YESSION_SESSION_WORK_NET`).
      ///
      /// An ask, never a grant. The operator's list is a ceiling and this may only narrow
      /// it: a file is authored by whoever can push to the repo, which is not whoever runs
      /// the host, so a repo that could widen its own reach would make the ceiling a
      /// suggestion. Reconciled in `Sandboxes`, where the two authors meet.
      Net : string list
      /// Extra host paths it asks to read, on the same terms.
      Read : string list
      Runtime : SandboxRuntime }

module EnvironmentSpec =

    /// The minimal built-in spec: session defaults everywhere.
    let defaults : EnvironmentSpec =
        { WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Net = []
          Read = []
          Runtime = Confinement }

    /// The same, as a container — what the docker backend starts from when nothing asked for
    /// anything in particular.
    let container : EnvironmentSpec =
        { defaults with Runtime = Container ContainerSpec.defaults }

/// One ask for a sandbox: what it should BE, and which credentials to forward into it.
///
/// Two fields rather than one because they are resolved by different parties — the spec is
/// what the session builds the sandbox from, `Forward` is a list of credential NAMES the
/// composition resolves against whoever is asking, at spawn, and never carries a value.
///
/// They travel together because together they are what "is this the same sandbox I already
/// have" compares. A comparison assembled at each call site is a comparison that will be
/// assembled differently at one of them, and the answer decides whether somebody's build
/// gets killed.
type SandboxRequest =
    { Spec : EnvironmentSpec
      Forward : string list }

module SandboxRequest =

    /// An ask that named nothing in particular — which is also what `default` is.
    let defaults : SandboxRequest = { Spec = EnvironmentSpec.defaults; Forward = [] }

    let private list (names: string list) =
        match names with
        | [] -> "nothing"
        | some -> String.concat ", " some

    let private mountsOf (runtime: SandboxRuntime) =
        match runtime with
        | Confinement -> []
        | Container container -> container.Mounts |> List.map (fun mount -> mount.Target)

    /// Every way what is RUNNING differs from what was asked for, each phrased as one
    /// clause of the refusal. Empty means the same ask, which is the idempotent case.
    ///
    /// Pure, and here rather than in the registry, because it is the entire content of the
    /// refusal: a registry that composed its own sentence would be a rule no cheap test
    /// could reach. It reports names, never values — an environment variable's value is
    /// the one thing a refusal must not print.
    /// TOTAL, and that is the property that matters: whenever the two differ this answers
    /// with at least one clause. A refusal that said "these differ" and then listed nothing
    /// would be worse than no refusal — and a `differences` that could come back empty on
    /// two unequal requests would let the registry mistake them for the same ask and hand
    /// back a sandbox nobody asked for.
    let differences (running: SandboxRequest) (wanted: SandboxRequest) : string list =
        let where (dir: string option) =
            match dir with
            | Some dir -> dir
            | None -> "wherever the sandbox puts it"
        let names (vars: Map<string, EnvironmentVariableRef>) =
            vars |> Map.toList |> List.map fst |> list
        let clauses =
            [ if running.Forward <> wanted.Forward then
                sprintf "it forwards %s, not %s" (list running.Forward) (list wanted.Forward)
              if running.Spec.WorkingDirectory <> wanted.Spec.WorkingDirectory then
                sprintf
                    "it starts in %s, not %s"
                    (where running.Spec.WorkingDirectory)
                    (where wanted.Spec.WorkingDirectory)
              if running.Spec.Net <> wanted.Spec.Net then
                sprintf "it reaches %s, not %s" (list running.Spec.Net) (list wanted.Spec.Net)
              if running.Spec.Read <> wanted.Spec.Read then
                sprintf "it reads %s, not %s" (list running.Spec.Read) (list wanted.Spec.Read)
              if running.Spec.EnvironmentVariables <> wanted.Spec.EnvironmentVariables then
                sprintf
                    "its environment sets %s, not %s"
                    (names running.Spec.EnvironmentVariables)
                    (names wanted.Spec.EnvironmentVariables)
              // The runtime is a union, so a difference in it can be the CASE as well as a
              // field — which is why it is described rather than compared field by field.
              if SandboxRuntime.describe running.Spec.Runtime <> SandboxRuntime.describe wanted.Spec.Runtime then
                sprintf
                    "it runs %s, not %s"
                    (SandboxRuntime.describe running.Spec.Runtime)
                    (SandboxRuntime.describe wanted.Spec.Runtime)
              // Mounts separately: two containers can share an image and differ entirely in
              // what they can see, and that is the difference somebody notices.
              if mountsOf running.Spec.Runtime <> mountsOf wanted.Spec.Runtime then
                sprintf
                    "it mounts %s, not %s"
                    (list (mountsOf running.Spec.Runtime))
                    (list (mountsOf wanted.Spec.Runtime)) ]
        // The backstop, and the reason this is a function rather than a comprehension at
        // the call site: the clauses above DESCRIBE a runtime rather than compare it field
        // by field, so two specs can differ somewhere none of them looks (a Dockerfile
        // path, say). Saying so vaguely is honest; saying nothing is not.
        match clauses with
        | [] when running <> wanted -> [ "it was started with a different configuration" ]
        | clauses -> clauses

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
