namespace Yession.Domain

/// The shell profile (Plan 25): one durable fact about a sandbox's terminals — where a
/// shell opened in it starts. Set by the agent, read by everyone, folded from the event
/// log like every other projection, so a restarted session opens its next terminal where
/// the last one started.
///
/// STRUCTURE, deliberately, rather than rc text a shell would source. A free-text profile
/// would be an ungated command running in every future terminal, authored once and never
/// seen again — around the classifier, around the queue people read, and out of the
/// transcript, since a shell that starts by running somebody's script has already run it
/// before the first prompt mark. `execute_command` is the one door; each field here is
/// applied by the SPAWN, never typed at a prompt.

type ShellProfile =
    { /// Where a shell opened under this profile starts. `None` = the sandbox's own
      /// default, which is what every terminal did before this plan.
      WorkingDirectory : string option }

module ShellProfile =

    /// No profile: the sandbox decides, as it always did.
    let none : ShellProfile = { WorkingDirectory = None }

/// Every sandbox's profile, projected from the log. Per sandbox and not per session
/// because a path is only a path inside the filesystem that has it: the default sandbox's
/// workspace, a named sandbox's, and a docker sandbox's bind are three different trees,
/// and one session-wide string would be a fact in one of them and a broken shell in the
/// rest.
type ShellProfileProjection =
    { Profiles : Map<SandboxName, ShellProfile> }

module ShellProfileProjection =

    let empty : ShellProfileProjection = { Profiles = Map.empty }

    /// Fold one event. The newest set wins, and a set carrying no directory REMOVES the
    /// entry rather than storing an empty profile — so "has a profile" and "starts
    /// somewhere" are the same question, which is the only way the query and the spawn can
    /// agree without either of them knowing about the other.
    let applyEvent (proj: ShellProfileProjection) (event: SessionEvent) : ShellProfileProjection =
        match event with
        | ShellProfileSet p ->
            match p.WorkingDirectory with
            | Some _ -> { Profiles = proj.Profiles |> Map.add p.Sandbox { WorkingDirectory = p.WorkingDirectory } }
            | None -> { Profiles = proj.Profiles |> Map.remove p.Sandbox }
        | _ -> proj

    let tryFind (sandbox: SandboxName) (proj: ShellProfileProjection) : ShellProfile option =
        proj.Profiles |> Map.tryFind sandbox

    /// Where a shell opened in this sandbox starts, if anywhere — what a spawn asks, and
    /// the only thing it asks.
    let workingDirectory (sandbox: SandboxName) (proj: ShellProfileProjection) : string option =
        proj |> tryFind sandbox |> Option.bind (fun profile -> profile.WorkingDirectory)

    /// The sandboxes that have one, in name order — the query's rows.
    let listed (proj: ShellProfileProjection) : (SandboxName * ShellProfile) list =
        proj.Profiles |> Map.toList |> List.sortBy (fst >> SandboxName.value)
