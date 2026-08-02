namespace Yession.Domain

/// The sandbox seam: one confined place a session runs processes. A session owns two
/// sibling sandboxes — the AgentSandbox hosting the agent CLI and the WorkSandbox
/// hosting agent-issued commands — both spawned by the Session Process and dying with
/// it. The Manager holds no environment authority; it keeps custody of secrets, which
/// cross to a session only at sandbox spawn (resolve-at-spawn, over the authenticated
/// control channel).

/// Which engine confines a sandbox's processes. Chosen once, at session boot, from
/// configuration — so an invalid choice fails the session loudly at start, never
/// silently mid-turn.
type SandboxBackend =
    /// Explicitly unsandboxed: plain child processes of the Session Process. The
    /// default for now; switching the default to a confined backend is a later,
    /// deliberate flip.
    | HostBackend
    /// OS-level confinement via `@anthropic-ai/sandbox-runtime` (bubblewrap on Linux,
    /// Seatbelt on macOS): wrapped spawn, millisecond start, enforced egress filtering.
    | SrtBackend
    /// A full isolated userland in a Docker container. Not the sub-second path; a
    /// hardened runtime (gVisor/Kata) is daemon configuration, invisible here.
    | DockerBackend

module SandboxBackend =

    /// Parse a configured backend name. Fail closed: anything unrecognised is a loud
    /// `Error` — a typo must never silently drop isolation.
    let parse (raw: string) : Result<SandboxBackend, string> =
        match raw.Trim().ToLowerInvariant () with
        | "host" -> Ok HostBackend
        | "srt" -> Ok SrtBackend
        | "docker" -> Ok DockerBackend
        | other -> Error (sprintf "unknown sandbox backend '%s' (expected host, srt, or docker)" other)

    let describe (backend: SandboxBackend) : string =
        match backend with
        | HostBackend -> "host"
        | SrtBackend -> "srt"
        | DockerBackend -> "docker"

    /// Parse the AgentSandbox backend: the agent CLI runs on host or under srt. Docker
    /// is BY DESIGN not an agent backend — a container per session boot is the
    /// opposite of the sub-second start the agent needs; the WorkSandbox keeps it.
    let parseAgent (raw: string) : Result<SandboxBackend, string> =
        parse raw
        |> Result.bind (function
            | DockerBackend -> Error "docker is a work-sandbox backend only — the agent sandbox is host or srt"
            | backend -> Ok backend)

/// Everything a sandbox needs to know at creation. `Env` is the sandbox's WHOLE base
/// environment — backends pass it verbatim and must never merge the parent process's
/// env over or under it (that merge is exactly the credential leak this seam removes).
type SandboxPolicy =
    { /// Paths the sandbox may read (confining backends only; host ignores them).
      ReadPaths : string list
      /// Paths the sandbox may write.
      WritePaths : string list
      /// Domains the sandbox may reach. None = unrestricted; Some [] = no egress.
      AllowedDomains : string list option
      Env : Map<string, string>
      /// Default working directory for spawns that do not name one.
      WorkingDirectory : string option }

/// One process to run inside a sandbox. `Env` is merged over the sandbox's policy env
/// (the request wins); there is no timeout here — callers race `Exited` and `Kill`.
type SandboxExec =
    { Executable : string
      Arguments : string list
      Env : Map<string, string>
      WorkingDirectory : string option }

/// How a sandboxed process ended.
type SandboxRun =
    /// The process ran and exited with this code (-1 when the OS reported none).
    | SandboxExited of code: int
    /// The process could not run, or its streams failed, for this reason.
    | SandboxRunFailed of reason: string

/// A live sandboxed process. Stdin is piped from day one — the agent CLI's stdio
/// transport and interactive work both ride this same handle.
type SandboxProcessHandle =
    { WriteStdin : string -> unit
      CloseStdin : unit -> unit
      /// Best-effort immediate termination of the process (and its tree, where the
      /// backend can).
      Kill : unit -> unit
      /// Resolves exactly once, when the process ends.
      Exited : Async<SandboxRun> }

/// One confined place to run processes. Created by, and dying with, its session.
type Sandbox =
    { /// The backend's reference for this sandbox (a container id, "host", ...) —
      /// what lifecycle events record.
      Ref : string
      /// Spawn a process, streaming its output through the callback in order.
      Spawn : SandboxExec -> (OutputStream * string -> unit) -> Async<Result<SandboxProcessHandle, string>>
      Dispose : unit -> Async<unit> }

/// Create a sandbox under a policy. The policy is assembled fresh per creation —
/// secret references resolve there, at spawn, and the plaintext goes nowhere else.
type CreateSandbox = SandboxPolicy -> Async<Result<Sandbox, string>>
