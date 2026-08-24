module Yession.Host.RepoSandboxes

// The fold: every configured repo's `yession.yaml`, into the commands the session already
// has (Plan 27).
//
// There is no executor here and deliberately no second policy engine. Plan 15 made every
// mutating command ENSURE-SHAPED specifically so this could be a fold rather than a
// runner: a declaration becomes one `start_work_sandbox`, put through the same gate the
// agent's goes through, and asking twice for what is already running changes nothing and
// records nothing. So convergence is a property of the commands, not of anything written
// here — and re-folding is free, which is what lets it run at boot and after every verb
// that changes what checkouts exist.
//
// What this DOES own is the part no command can: which files there are, which of them could
// be read, and what happened to each declaration. A fold that silently did nothing for a
// repo whose file has a typo would be the exact failure the schema's strict decoding exists
// to avoid, one layer up.
//
// It never stops anything. A declaration that disappears leaves its sandbox running and
// marked as no longer declared: convergence that kills somebody's build is not convergence
// (`WorkSandboxes.fs`), and removal stays `stop_work_sandbox`.

open Yession.Domain
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.SessionProcess

/// What the last fold made of one repo. `Sandbox = None` is about the FILE itself — it
/// could not be read — and the distinction matters because those are fixed in different
/// places: one by whoever wrote the YAML, the other by whoever wrote the sandbox.
type FoldOutcome =
    { Repo : RepoRef
      Sandbox : SandboxRef option
      /// `None` is fine. There is no third state: a declaration either became a sandbox or
      /// has a reason it did not.
      Problem : string option }

type RepoSandboxes =
    { /// Re-read every checkout and ensure what it declares, on the authority of whoever
      /// asked. `None` is the fold at boot, which nobody triggered.
      Fold : ActorRef option -> Async<unit>
      /// What the last fold made of each repo, in a stable order — the query's rows.
      Outcomes : unit -> FoldOutcome list
      /// Sandboxes this session is running that no file declares any more. Named rather
      /// than stopped.
      Undeclared : unit -> SandboxRef list }

/// A session with nothing to fold: no repos service, or a composition without one. Total,
/// so a caller never branches on whether the fold exists.
let none : RepoSandboxes =
    { Fold = fun _ -> async { return () }
      Outcomes = fun () -> []
      Undeclared = fun () -> [] }

let create
    (reposDir: string)
    (repos: unit -> Repos.ReposService option)
    (sandboxes: unit -> WorkSandboxes.WorkSandboxes)
    (run: RunGatedCommand)
    : RepoSandboxes =

    let mutable outcomes : FoldOutcome list = []
    // Which refs the last fold saw declared. Compared against what is RUNNING to answer
    // "no longer declared", which is a question about the difference between the two and
    // so belongs to whoever holds both.
    let mutable declaredRefs : Set<string> = Set.empty

    let fold (onBehalfOf: ActorRef option) : Async<unit> =
        async {
            match repos () with
            | None ->
                outcomes <- []
                declaredRefs <- Set.empty
            | Some service ->
                match! service.ListRepos () with
                // A listing that failed says nothing about any repo in particular, so there
                // is no row to write and the previous answer stands: reporting "no repo
                // declares anything" because the disk hiccupped would be a worse lie than
                // saying nothing.
                | Error _ -> ()
                | Ok listings ->
                    let declared, unreadable =
                        RepoConfig.readAll reposDir (listings |> List.map (fun listing -> listing.Repo))
                    let fileProblems =
                        unreadable
                        |> List.map (fun (repo, reason) ->
                            { Repo = repo; Sandbox = None; Problem = Some reason })
                    let! declarations =
                        declared
                        |> Map.toList
                        |> List.map (fun (ref, decl) ->
                            async {
                                match SandboxRef.scope ref with
                                // Every key in the map came from `scoped`, which only ever
                                // writes a repo scope. A session-owned one here would mean
                                // the union had been fed something no file produced.
                                | SessionOwned -> return None
                                | RepoOwned repo ->
                                    let call =
                                        Commands.startWorkSandboxCall
                                            (Authority.configuredBy repo onBehalfOf)
                                            ref
                                            decl
                                    match! run call with
                                    | Error reason ->
                                        return Some { Repo = repo; Sandbox = Some ref; Problem = Some reason }
                                    | Ok outcome ->
                                        // The gate answers with what happened, and a
                                        // command that ran and failed is not a command that
                                        // was refused — both are reasons this declaration
                                        // is not a sandbox, and the row says which.
                                        let problem =
                                            match outcome.Status with
                                            | CommandRefusedBy (_, reason) ->
                                                Some (defaultArg reason "refused")
                                            | CommandRan text when text.StartsWith "failed: " ->
                                                Some (text.Substring 8)
                                            | CommandRan _
                                            | CommandRunning -> None
                                        return Some { Repo = repo; Sandbox = Some ref; Problem = problem }
                            })
                        |> Async.Sequential
                    outcomes <- fileProblems @ (declarations |> Array.toList |> List.choose id)
                    declaredRefs <- declared |> Map.toList |> List.map (fst >> SandboxRef.render) |> Set.ofList
        }

    let undeclared () =
        (sandboxes ()).Listed ()
        |> List.map (fun entry -> entry.Ref)
        // Only a REPO's sandbox can stop being declared. The session's own were never
        // declared by a file, so they are not undeclared by one either.
        |> List.filter (fun ref ->
            match SandboxRef.scope ref with
            | SessionOwned -> false
            | RepoOwned _ -> not (Set.contains (SandboxRef.render ref) declaredRefs))

    { Fold = fold
      Outcomes = fun () -> outcomes
      Undeclared = undeclared }

// --- the `repo_config` query ------------------------------------------------------------

let queryName : QueryName = QueryName.create "repo_config" |> Result.defaultWith failwith

let private queryDef : QueryDef =
    { Name = queryName
      Title = "repo config"
      Description =
        "What each checkout's yession.yaml asked this session for, and what came of it. A \
         row with no problem is a declaration that became a sandbox; a row naming one is a \
         declaration that did not, and says why. A sandbox listed as no longer declared is \
         still running — removing one is stop_work_sandbox's job, never a fold's."
      Shape =
        Rows
            [ QueryColumn.create "repo" "repo"
              QueryColumn.create "sandbox" "sandbox"
              QueryColumn.create "state" "state"
              QueryColumn.create "problem" "problem" ] }

/// Register the fold's answer as a query.
///
/// A QUERY and not a stream of notes, and the difference is the accumulation. A fold runs
/// at boot and after every repo verb, so a note per outcome would write the same sentence
/// on every trigger — which is the thing Plan 17's declaration-delta rule exists to stop.
/// The starts that DID happen already have act-lines of their own, once each, because
/// `WorkSandboxes.ensure` records nothing on a repeat. What is left is live state, and live
/// state belongs in a query where a re-fold costs nothing and a restart shows the truth.
let query (current: unit -> RepoSandboxes) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                let folded = current ()
                let declarations =
                    folded.Outcomes ()
                    |> List.map (fun outcome ->
                        [ "repo", CellText (RepoRef.value outcome.Repo)
                          "sandbox",
                          (match outcome.Sandbox with
                           | Some sandbox -> CellText (SandboxRef.render sandbox)
                           | None -> CellAbsent)
                          "state",
                          CellText (
                              match outcome.Sandbox, outcome.Problem with
                              | None, _ -> "unreadable"
                              | Some _, None -> "declared"
                              | Some _, Some _ -> "not started")
                          "problem",
                          (match outcome.Problem with
                           | Some problem -> CellText problem
                           | None -> CellAbsent) ])
                let orphans =
                    folded.Undeclared ()
                    |> List.map (fun sandbox ->
                        [ "repo",
                          (match SandboxRef.scope sandbox with
                           | RepoOwned repo -> CellText (RepoRef.value repo)
                           | SessionOwned -> CellAbsent)
                          "sandbox", CellText (SandboxRef.render sandbox)
                          "state", CellText "no longer declared"
                          "problem", CellAbsent ])
                return Ok (RowsOf (declarations @ orphans))
            } }
