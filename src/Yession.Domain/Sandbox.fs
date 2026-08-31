namespace Yession.Domain.Sandboxes

open Yession.Domain

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
    /// Explicitly unsandboxed: plain child processes of the Session Process. No longer
    /// the default — it has to be asked for, and it is honest about what it is: the env
    /// allowlist still holds, the filesystem and the network do not.
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

/// Whether a sandbox confines the FILESYSTEM at all. Egress, env and process
/// confinement are unaffected either way — this is only about paths.
///
/// `Unconfined` exists for one caller: the repo clone. srt refuses writes to
/// `.git/hooks`, `.git/config`, `.vscode`, `.idea`, `.claude/commands|agents`,
/// `.mcp.json`, `.gitmodules` and the shell rc names WHEREVER they appear, and no
/// allow-path outranks that refusal — so a checkout containing any of them cannot be
/// written by a confined process. The refusal is a set of patterns on macOS and a scan
/// of what already exists on Linux, which is why the same clone succeeds there; we take
/// the weaker state on BOTH rather than run a path in production that no CI here
/// exercises.
///
/// UNDO when srt can exempt a SUBTREE from those refusals (or macOS adopts the Linux
/// scan, which exempts a not-yet-existing checkout by construction): then the clone takes
/// the ordinary confined policy and this case has no callers left — delete it, rather
/// than leave an unused way to turn the filesystem off.
type FilesystemConfinement =
    | Confined
    | Unconfined

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
      /// Every place this host could not give exactly what the resource named.
      ///
      /// On the policy because it has two readers and they must not disagree: a BACKEND reads
      /// it to do the coarser thing it said it would do (srt allows every unix socket when it
      /// could not scope one), and the session reads it to tell whoever is working here what
      /// they actually hold. A degradation the backend acts on and nobody is told about is
      /// the fault this exists to remove; one reported and not acted on is a lie the other
      /// way.
      ///
      /// Empty on a host that can express the whole selection, which is the common case and
      /// says nothing rather than saying "no degradations".
      Realisation : (ResourceLeaf * LeafRealisation) list
      /// Unix sockets the sandbox may CONNECT to.
      ///
      /// Its own axis, because connecting to a socket is its own permission and the file
      /// halves do not add up to it: macOS needs `network-outbound` on the path, which is
      /// granted separately from reading or writing the node. Measured — a sandbox holding
      /// the nix daemon socket readable and writable, with `test -S` passing, still could
      /// not talk to it: "could not connect to any lix socket".
      Sockets : string list
      /// The granted mounts VERBATIM — from, at, mode — beside the flattened path lists
      /// rather than instead of them, because the two backend families consume different
      /// questions from one grant: the confining host family closes path SETS over the
      /// host's own spellings, while a container backend materialises each mount at its
      /// declared target. Both fields are filled from the same leaves in one place
      /// (`Sandboxes.policyFor`), which is what keeps them one fact.
      Binds : ResourceMount list
      /// Granted named volumes, as (volume, target). Only a container backend can hold
      /// one — everywhere else the leaf is withheld before a policy exists — so a backend
      /// that ignores this field is a backend that never receives it filled.
      Volumes : (string * string) list
      Env : Map<string, string>
      /// Where this sandbox stands: an ABSOLUTE directory, and the two things that
      /// depend on it are why it has to be.
      ///
      /// It is the directory a spawn naming none runs in, and it is the root every
      /// spawn that DOES name one is resolved against (`SandboxPath.resolvedFrom`, in
      /// each backend). A relative value breaks both — the backend creates it against
      /// its own process's cwd rather than the session's, and every per-spawn
      /// resolution stays relative on top of it.
      ///
      /// So the outside vocabulary stops at `policyFor`, which holds the root to
      /// resolve against. Nothing downstream re-resolves this, and nothing should.
      WorkingDirectory : string option
      /// Whether the paths above are enforced at all. `Confined` everywhere except the
      /// clone sandbox — see `FilesystemConfinement`.
      Filesystem : FilesystemConfinement }

/// One process to run inside a sandbox. `Env` is merged over the sandbox's policy env
/// (the request wins); there is no timeout here — callers race `Exited` and `Kill`.
type SandboxExec =
    { Executable : string
      Arguments : string list
      Env : Map<string, string>
      /// Where the process runs, in the vocabulary everything outside the sandbox speaks
      /// (`SandboxPath`): relative to where the sandbox puts a process, or absolute. The
      /// BACKEND resolves it — see `SandboxPath.resolvedFrom` — because the sandbox is the
      /// only thing that knows its own root. `None` = wherever the policy puts it.
      WorkingDirectory : string option }

/// The path vocabulary everything OUTSIDE a sandbox speaks: a directory as a terminal in
/// that sandbox reaches it — relative to where a shell there starts when it is under
/// there, absolute when it is not.
///
/// `repos/octo/hello` is the whole path anybody in the session can act on, and it stays
/// that length however long the operator's data directory is. The absolute form is the
/// same fact wearing somebody's home directory —
/// `/Users/someone/.yession/sessions/40V9FY6MT534HDMBX6W5HS8PGR/workspace/repos/…` — which
/// every answer then carries and nobody who reads it can do anything with.
///
/// BOTH directions live here, in one module, because they are one fact seen twice: what
/// `reachedFrom` hands out, `resolvedFrom` has to take back. Converted in two places they
/// converted differently, and did — the repo verbs answered `repos/octo/hello` while the
/// shell profile stored what the sandbox's `pwd` said, so the note about where terminals
/// start wore the operator's home directory, and deleting that very checkout matched no
/// profile and cleared none.
module SandboxPath =

    /// A path names a directory whether or not it carries a trailing slash, and which it
    /// is says nothing about what it means.
    let private trimmed (path: string) = path.TrimEnd '/'

    /// An absolute path as a terminal REACHES it: relative to `root` when it is under
    /// there, unchanged when it is not — a docker sandbox's `/repos` bind, a named
    /// sandbox reaching the shared repos directory from its own workspace, anywhere else
    /// on the filesystem. A relative path that is only true somewhere else is worse than
    /// a long one.
    ///
    /// On a directory BOUNDARY, never a bare prefix: `/data/s/work` does not contain
    /// `/data/s/workspace/repos`, and answering `space/repos` for it would be a path to
    /// nowhere.
    ///
    /// The root ITSELF is `.` — the one relative path that is always true, and the only
    /// other answer would be the absolute one this exists to keep out of sight.
    let reachedFrom (root: string option) (path: string) : string =
        match root with
        | Some root when trimmed path = trimmed root -> "."
        | Some root when path.StartsWith (trimmed root + "/") -> path.Substring ((trimmed root).Length + 1)
        | _ -> path

    /// The absolute directory a spawn actually runs in: `path` resolved against the
    /// sandbox's own `root`. THE one place a relative path in this vocabulary acquires its
    /// meaning, and it acquires it from the sandbox — the only thing that knows what its
    /// root is.
    ///
    /// An absolute path is already an answer. `None` is a caller with no opinion, which is
    /// the root itself.
    let resolvedFrom (root: string option) (path: string option) : string option =
        match path, root with
        | None, _ -> root
        | Some path, _ when path.StartsWith "/" -> Some path
        // `reachedFrom`'s answer for the root itself, taken back — the two arms are one
        // mapping, and a round trip that returned `/ws/.` would be that mapping losing.
        | Some ".", Some root -> Some root
        | Some path, Some root -> Some (sprintf "%s/%s" (trimmed root) path)
        // A relative path and no root: the backend's own idea of where it stands is the
        // only thing left, which is what an unconfined spawn has always used.
        | Some path, None -> Some path

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

/// A live process on a PSEUDO-TERMINAL (Plan 13, stage 2c).
///
/// Distinct from `SandboxProcessHandle` because a pty is not a pipe, and the difference is
/// not cosmetic: a pty has a line discipline, a size, and a controlling terminal, so a
/// program can ask whether it is interactive, redraw on `SIGWINCH`, and take the alternate
/// screen. Over pipes most full-screen programs refuse or degrade — which is why a terminal
/// that wants to run `vim` needs this shape and not the other one.
///
/// There is no stdout/stderr split, and that is a property of ptys rather than an omission:
/// both streams share one terminal device, which is what a tty IS. The piped handle keeps
/// the distinction because it genuinely has it.
type PtyHandle =
    { /// Raw bytes to the pty master — a command line, a keystroke, a control character.
      Write : string -> unit
      /// Tell the pty its new size, which is what raises `SIGWINCH` in the foreground
      /// program. A terminal whose program never learns its size is the one that redraws
      /// wrongly.
      Resize : int -> int -> unit
      Kill : unit -> unit
      /// Resolves exactly once, when the process ends.
      Exited : Async<SandboxRun> }

/// One thing that has to be true before a sandbox is worth handing to anybody.
///
/// `Probe` is shell, and it says nothing: it exits 0 or it does not. What a person reads is
/// `What` and `Because`, which is why they are here rather than inferred from a failing
/// command's stderr — the faults this catches produce no stderr worth reading, and several
/// produce none at all.
type SandboxCheck =
    { /// What is being asserted, in the words of whoever has to fix it.
      What : string
      /// A `/bin/sh` expression that exits 0 when `What` holds.
      Probe : string
      /// What stops working when it does not.
      Because : string }

/// What must hold before a sandbox is declared started.
///
/// Every fault this catches was MEASURED as a silent one. A sandbox granted
/// `/etc/ssl/cert.pem` on macOS is denied it, because `/etc` is a symlink and the grant
/// canonicalises past it; a sandbox whose `TMPDIR` is srt's default cannot write it, for the
/// same reason; a sandbox that inherits the operator's `HOME` cannot write a byte of it.
/// None of the three says anything at the point it is wrong. The first two now cannot happen
/// — they are refused where the path is written — and this is what catches the NEXT one: a
/// different cause arriving at the same place, turned into a sentence instead of a tool
/// failing strangely an hour later.
///
/// Ordered most-upstream first, and only the FIRST failure is reported, because the rest are
/// usually its consequences. A HOME that cannot be written makes half the list fail, and a
/// report naming all of them buries the one that explains the others.
module SandboxVerification =

    /// Shell-quote for `/bin/sh`. Paths here come from an operator's file and a repo's
    /// selection, so they are not trusted to be free of spaces — or of quotes.
    let private quoted (value: string) : string =
        "'" + value.Replace ("'", "'\''") + "'"

    /// The checks for this policy, in the order they should run.
    ///
    /// Pure, and that is the point: WHICH checks a policy deserves is decided here, where a
    /// test can read the list without a sandbox to run it in.
    ///
    /// Deliberately not checked: whether an endpoint answers. That is seconds per host, and
    /// it turns a registry having a bad minute into a sandbox that refuses to start — a check
    /// that fails when the thing checked is fine is worse than no check at all.
    let plan (policy: SandboxPolicy) : SandboxCheck list =
        match policy.Filesystem with
        // Nothing to verify where nothing is enforced. A check that always passes reads as
        // coverage and is not.
        | Unconfined -> []
        | Confined ->
            let home = policy.Env |> Map.tryFind "HOME"
            let tmp = policy.Env |> Map.tryFind "TMPDIR"
            // A place that cannot be written is reported as the FIRST thing wrong with it,
            // so `HOME` and `TMPDIR` lead: every toolchain keeps state in one or the other,
            // and a failure in either makes everything after it fail too.
            // `touch` and `>>` rather than `test -w`: on a Seatbelt host `test -w` consults
            // the file MODE, which is the operator's and says yes, while the write itself is
            // refused by the sandbox profile. The question is whether a write SUCCEEDS, so
            // the probe writes.
            //
            // Three shapes, because a write path is not always a directory and the wrong
            // probe is a false alarm — measured: a granted nix daemon socket is a write path
            // (a socket is read AND written by anything that talks to it), and asking
            // whether a file could be created inside it refused a sandbox that was fine.
            //
            // The third arm is a known LIMIT, not a check: for a socket or a device, "can
            // this be written" is "can this be connected to", which cannot be asked without
            // doing it. Existence is all this can honestly assert there, and a socket whose
            // far end is gone will pass. Said out loud rather than left to be discovered.
            let writable (what: string) (path: string) (because: string) =
                // Phrased as the FAILURE, because that is the only state this is ever
                // read in. A claim rendered in the affirmative and then reported as a
                // refusal reads, at a glance, as the opposite of what happened.
                { What = sprintf "%s (%s) is not usable as granted" what path
                  Probe =
                    sprintf
                        "d=%s; if [ -d \"$d\" ] || [ ! -e \"$d\" ]; then mkdir -p \"$d\" && touch \"$d/.yession-check\" && rm -f \"$d/.yession-check\"; elif [ -f \"$d\" ]; then : >> \"$d\"; else test -e \"$d\"; fi"
                        (quoted path)
                  Because = because }
            let readable (path: string) =
                { What = sprintf "%s cannot be read" path
                  Probe = sprintf "test -r %s" (quoted path)
                  Because = "it was granted to this sandbox, and something that was granted and is not held is the fault this check exists for" }
            [ match home with
              | Some path ->
                  yield writable "the sandbox's home" path
                            "every toolchain keeps state under $HOME, and a tool given a home it cannot touch fails where a tool given none would have fallen back"
              | None -> ()
              match tmp with
              | Some path ->
                  yield writable "the sandbox's temporary directory" path
                            "anything that writes a temporary file — a compiler, an archive tool, a package manager — writes here"
              | None -> ()
              for path in policy.WritePaths do
                  yield writable "a granted path" path
                            "it was granted writable, and a grant that is not held is the fault this check exists for"
              // Read paths after the writable ones, because everything writable is also
              // readable and a read that fails on a path that could not be written says the
              // less useful half of the same thing.
              for path in policy.ReadPaths do
                  if not (List.contains path policy.WritePaths) then yield readable path ]

    /// One shell program that runs the whole list and names the first thing that failed.
    ///
    /// ONE spawn, not one per check. A wrapped spawn costs tens of milliseconds and this runs
    /// on the path to every sandbox start, so a list of twenty checks is the difference
    /// between a rounding error and something a person notices.
    ///
    /// Exits 0 when everything holds; otherwise prints the index of the first failure and
    /// exits non-zero, which is the only thing the caller has to parse.
    let program (checks: SandboxCheck list) : string =
        checks
        |> List.mapi (fun index check ->
            sprintf "if ! { %s; } >/dev/null 2>&1; then echo %d; exit 1; fi" check.Probe index)
        |> String.concat "\n"

    /// Whether the probe's output NAMES one of the checks — the difference between a
    /// verdict (a check ran and failed) and noise (the shell never ran the program:
    /// a daemon-level exec failure, an empty stream). A caller retries noise and
    /// believes a verdict.
    let named (checks: SandboxCheck list) (output: string) : bool =
        match System.Int32.TryParse (output.Trim ()) with
        | true, index -> index >= 0 && index < List.length checks
        | _ -> false

    /// The sentence for a failure at `index`, or a fallback when the probe said something
    /// this code cannot place.
    ///
    /// Says what nothing after it was checked, because that is true and because a person
    /// reading one fault out of a list of twenty will otherwise assume the other nineteen
    /// passed.
    let explain (checks: SandboxCheck list) (output: string) : string =
        let said = output.Trim ()
        match System.Int32.TryParse said with
        | true, index when index >= 0 && index < List.length checks ->
            let check = List.item index checks
            sprintf "%s — %s. Nothing after that was checked." check.What check.Because
        | _ ->
            sprintf
                "a start-up check failed and did not say which: %s"
                (if said = "" then "it printed nothing" else said)

/// One confined place to run processes. Created by, and dying with, its session.
type Sandbox =
    { /// The backend's reference for this sandbox (a container id, "host", ...) —
      /// what lifecycle events record.
      Ref : string
      /// Spawn a process, streaming its output through the callback in order.
      Spawn : SandboxExec -> (OutputStream * string -> unit) -> Async<Result<SandboxProcessHandle, string>>
      /// Spawn a process on a pty of the given size, streaming everything the terminal
      /// emits through the callback (Plan 13, stage 2c).
      ///
      /// An OPTION because pty support is genuinely per-backend, not because it is
      /// optional to care about. docker has it free (exec with `Tty: true`, plus the
      /// exec-resize endpoint); the host needs a native addon that may not be installed;
      /// srt wraps the host spawn and so inherits whatever the host answered. `None` is a
      /// backend saying it cannot host a live terminal — declared up front rather than
      /// discovered when someone runs `vim`, which is the same declare-and-skip honesty the
      /// capability-tagged test tiers use.
      SpawnPty : (SandboxExec -> int -> int -> (string -> unit) -> Async<Result<PtyHandle, string>>) option
      Dispose : unit -> Async<unit> }

/// Create a sandbox under a policy. The policy is assembled fresh per creation —
/// secret references resolve there, at spawn, and the plaintext goes nowhere else.
type CreateSandbox = SandboxPolicy -> Async<Result<Sandbox, string>>
