namespace Yession.Domain.Repos

open Yession.Domain

/// The session's repos, projected from events (Plan 14). The agent's git verbs and the
/// settings panel are two interfaces over one Process-side function; this projection is
/// the read model both render — deterministic, a pure function of the ordered event
/// sequence, like `EnvironmentStatus`.

type SessionRepo =
    { Repo : RepoRef
      /// The branch the checkout is on, per the last recorded add/switch. The FILESYSTEM
      /// is the truth (a terminal can move a branch without an event); this is the last
      /// state the repo manager recorded, which the manager itself reconciles on list.
      Branch : string
      /// Who brought the repo in — what the shared-trust disclosure names.
      AddedBy : ActorRef }

type ReposProjection =
    { Repos : SessionRepo list }

module ReposProjection =

    let empty : ReposProjection = { Repos = [] }

    /// Fold one event. Re-adding an existing repo replaces its entry (the clone was
    /// re-verified; the newest add wins), keeping list order stable otherwise.
    let applyEvent (proj: ReposProjection) (event: SessionEvent) : ReposProjection =
        match event with
        | RepoAdded r ->
            let entry = { Repo = r.Repo; Branch = r.Branch; AddedBy = r.Actor }
            if proj.Repos |> List.exists (fun s -> s.Repo = r.Repo) then
                { Repos = proj.Repos |> List.map (fun s -> if s.Repo = r.Repo then entry else s) }
            else
                { Repos = proj.Repos @ [ entry ] }
        | RepoRemoved r ->
            { Repos = proj.Repos |> List.filter (fun s -> s.Repo <> r.Repo) }
        | RepoBranchSwitched r ->
            { Repos =
                proj.Repos
                |> List.map (fun s -> if s.Repo = r.Repo then { s with Branch = r.Branch } else s) }
        | _ -> proj

    let tryFind (repo: RepoRef) (proj: ReposProjection) : SessionRepo option =
        proj.Repos |> List.tryFind (fun s -> s.Repo = repo)

/// One repo as the verbs and the panel report it: the FILESYSTEM's answer, not the
/// projection's — the checkout is the truth, and a listing that disagreed with `git
/// status` would teach everyone to distrust the panel.
type RepoListing =
    { Repo : RepoRef
      Branch : string
      /// Uncommitted changes present. Told, not fixed: resume drift is a fact to
      /// surface, never something a list call quietly cleans.
      Dirty : bool
      /// Where the checkout is, as a TERMINAL in this session sees it — the one fact an
      /// agent needs to do anything with a repo it just cloned, and the one nothing used
      /// to tell it. Answering "added octo/hello" and leaving the path to be guessed is
      /// how a clone turns into `ls ~/repos`, twice, and then a turn that ran out of steps.
      Path : string }

module RepoListing =

    /// Render one listing line the way both interfaces say it.
    let describe (listing: RepoListing) : string =
        sprintf
            "%s (branch %s%s) at %s"
            (RepoRef.value listing.Repo)
            listing.Branch
            (if listing.Dirty then ", uncommitted changes" else "")
            listing.Path
