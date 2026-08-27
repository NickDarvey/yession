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
open Yession.Domain.Repos
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.SessionProcess

/// What a repo's selection comes to: the lines a person reads, and whether any of it is
/// something the operator marked as worth being asked about.
type RepoCapabilities =
    { Granted : string list
      Sensitive : bool }

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
      Undeclared : unit -> SandboxRef list
      /// Consent to what a repo asks for, on the authority of a person.
      ///
      /// Takes the set the person was SHOWN and refuses if it is not the set asked for now.
      /// A capability set is authored by whoever can push to the checkout and can change
      /// between a screen being drawn and a button being pressed — approving something other
      /// than what was read is the failure worth preventing, and it is the easy one to build.
      Approve : ActorRef -> RepoRef -> string list -> Async<Result<unit, string>> }

/// A session with nothing to fold: no repos service, or a composition without one. Total,
/// so a caller never branches on whether the fold exists.
let none : RepoSandboxes =
    { Fold = fun _ -> async { return () }
      Outcomes = fun () -> []
      Undeclared = fun () -> []
      Approve = fun _ _ _ -> async { return Error "this session has no repos to approve anything for" } }

/// What the log already says about one declaration: the reason it was last refused, when
/// that refusal is still the last word about it.
///
/// A start ENDS a refusal rather than merely following it, so a failure after one is news
/// again — the same rule `SessionEnvironment` applies to its own start attempts, and for the
/// same reason: what is stale is a refusal that is still true, not every refusal ever made.
///
/// Read from the log rather than from `outcomes`, which is the field beside it. `outcomes`
/// is what the LAST fold made of things and is empty in a process that has just started, so
/// a delta against it would re-announce every standing refusal on every restart. The log is
/// what the session has actually been told.
let private lastRefusal (events: SessionEvent list) (repo: RepoRef) (sandbox: SandboxRef option) : string option =
    let rendered = sandbox |> Option.map SandboxRef.render
    events
    |> List.choose (fun event ->
        match event with
        | SessionEvent.RepoConfigRefused r when
            RepoRef.value r.Repo = RepoRef.value repo
            && (r.Sandbox |> Option.map SandboxRef.render) = rendered -> Some (Some r.Reason)
        | SessionEvent.WorkSandboxStarted started when Some (SandboxRef.render started.Sandbox) = rendered ->
            Some None
        | _ -> None)
    |> List.tryLast
    |> Option.flatten

/// What the log already says this repo asks for.
///
/// The LOG rather than a field, for the reason every delta in this file uses it: a field is
/// empty in a process that has just started, so a delta against one would re-announce every
/// repo's capabilities on every restart — which is the opposite of the point, since a
/// re-announcement nobody caused trains people to stop reading them.
let private lastCapabilities (events: SessionEvent list) (repo: RepoRef) : string list option =
    events
    |> List.choose (fun event ->
        match event with
        | SessionEvent.RepoCapabilitiesChanged c when RepoRef.value c.Repo = RepoRef.value repo ->
            Some c.Granted
        | _ -> None)
    |> List.tryLast

/// What a person last consented to for this repo.
let private lastApproved (events: SessionEvent list) (repo: RepoRef) : string list option =
    events
    |> List.choose (fun event ->
        match event with
        | SessionEvent.RepoCapabilitiesApproved a when RepoRef.value a.Repo = RepoRef.value repo ->
            Some a.Granted
        | _ -> None)
    |> List.tryLast

let create
    (reposDir: string)
    (repos: unit -> Repos.ReposService option)
    (sandboxes: unit -> WorkSandboxes.WorkSandboxes)
    (run: RunGatedCommand)
    (log: EventLog<SessionEvent>)
    // What a selection of resource names comes to. The fold needs it because a capability
    // set is a fact about a REPO, not about one of its sandboxes, and only the fold sees all
    // of a repo's declarations at once.
    //
    // Sensitivity is REPORTED rather than inferred from the words: reading it back out of a
    // rendered description would make the prompt depend on the wording of a sentence, and the
    // wording is a design that will move.
    (capabilitiesOf: ResourceName list -> Result<RepoCapabilities, string>)
    : RepoSandboxes =

    let mintMessageId () : MessageId =
        match MessageId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "message id invariant violated: %s" e

    let append (actor: ActorRef) (event: SessionEvent) : Async<unit> =
        async {
            let! _ = log.Append actor event
            return ()
        }

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
                    // What each repo asks for, said when it changes — before anything is
                    // started, so the timeline reads in the order things happened rather
                    // than announcing a set after the sandboxes it describes came up.
                    let! page = log.Read None System.Int32.MaxValue
                    let told = page.Events |> List.map (fun e -> e.Event)
                    let byRepo =
                        declared
                        |> Map.toList
                        |> List.choose (fun (ref, decl) ->
                            match SandboxRef.scope ref with
                            | SessionOwned -> None
                            | RepoOwned repo -> Some (repo, decl))
                        |> List.groupBy (fun (repo, _) -> RepoRef.value repo)
                    for _, entries in byRepo do
                        let repo = entries |> List.head |> fst
                        let asked =
                            entries
                            |> List.collect (fun (_, decl) -> decl.Uses)
                            |> List.distinct
                        match capabilitiesOf asked with
                        // A selection that does not resolve is already a refusal further
                        // down, said per declaration and with the reason. Saying it twice
                        // here, in different words, would be two accounts of one fault.
                        | Error _ -> ()
                        | Ok capabilities ->
                            let granted = capabilities.Granted
                            if lastCapabilities told repo <> Some granted then
                                let actor = ActorRef.Configured repo
                                do!
                                    append
                                        actor
                                        (SessionEvent.RepoCapabilitiesChanged
                                            { RepoCapabilitiesChanged.MessageId = mintMessageId ()
                                              RepoCapabilitiesChanged.Repo = repo
                                              RepoCapabilitiesChanged.Granted = granted
                                              RepoCapabilitiesChanged.Sensitive = capabilities.Sensitive
                                              RepoCapabilitiesChanged.Actor = actor })
                    // Which repos are waiting on somebody. Only a SENSITIVE set waits: a
                    // repo asking for a cache and a store is not a decision anybody wants to
                    // be asked to make, and a prompt that appears for everything is a prompt
                    // people learn to dismiss without reading — which is worse than none,
                    // because it launders the one that mattered.
                    let awaiting =
                        byRepo
                        |> List.choose (fun (_, entries) ->
                            let repo = entries |> List.head |> fst
                            let asked = entries |> List.collect (fun (_, decl) -> decl.Uses) |> List.distinct
                            match capabilitiesOf asked with
                            | Ok capabilities when
                                capabilities.Sensitive && lastApproved told repo <> Some capabilities.Granted ->
                                Some (RepoRef.value repo, capabilities.Granted)
                            | _ -> None)
                        |> Map.ofList
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
                                | RepoOwned repo when Map.containsKey (RepoRef.value repo) awaiting ->
                                    // Nothing starts until somebody says yes. The row says
                                    // what is being asked, so the answer to "why is my
                                    // sandbox not running" is in the same place as what to do
                                    // about it.
                                    return
                                        Some
                                            { Repo = repo
                                              Sandbox = Some ref
                                              Problem =
                                                Some (
                                                    sprintf
                                                        "waiting for somebody in this session to approve what %s asks for: %s"
                                                        (RepoRef.value repo)
                                                        (String.concat "; " (Map.find (RepoRef.value repo) awaiting))) }
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
                    // Say the refusals that are NEW. A start already announces itself, so
                    // without this the two outcomes of a declaration were split — one on the
                    // timeline, one behind a query nobody opens until they suspect a problem,
                    // which is the state a broken file leaves you least able to suspect.
                    //
                    // On change, never per fold: this runs after every repo verb, so a note
                    // per outcome would be the accumulation the query was chosen to avoid in
                    // the first place, rebuilt on the surface it was avoided for.
                    let! page = log.Read None System.Int32.MaxValue
                    let told = page.Events |> List.map (fun e -> e.Event)
                    for outcome in outcomes do
                        match outcome.Problem with
                        | None -> ()
                        | Some reason when lastRefusal told outcome.Repo outcome.Sandbox = Some reason -> ()
                        | Some reason ->
                            // Attributed to the file, like the starts it did not produce.
                            // `onBehalfOf` is whoever triggered the fold, and it is not
                            // borrowed here: nothing is being done on their authority — this
                            // is the file reporting that nothing was.
                            let actor = ActorRef.Configured outcome.Repo
                            do!
                                append
                                    actor
                                    (SessionEvent.RepoConfigRefused
                                        { RepoConfigRefused.MessageId = mintMessageId ()
                                          RepoConfigRefused.Repo = outcome.Repo
                                          RepoConfigRefused.Sandbox = outcome.Sandbox
                                          RepoConfigRefused.Reason = reason
                                          RepoConfigRefused.Actor = actor })
        }

    /// What this repo asks for right now, or nothing when its selection does not resolve.
    let askedBy (declared: Map<SandboxRef, SandboxDecl>) (repo: RepoRef) =
        declared
        |> Map.toList
        |> List.choose (fun (ref, decl) ->
            match SandboxRef.scope ref with
            | RepoOwned owner when RepoRef.value owner = RepoRef.value repo -> Some decl.Uses
            | _ -> None)
        |> List.collect id
        |> List.distinct
        |> capabilitiesOf
        |> Result.toOption
        |> Option.map (fun capabilities -> capabilities.Granted)

    /// Consent to what a repo asks for.
    ///
    /// A PERSON may, and `ActorRef` already says which kind: `UserRef` is somebody who signed
    /// in, `PeerRef` somebody at a browser who did not. Both are people, and the record keeps
    /// them apart rather than flattening one into the other — a deployment that trusts
    /// whoever reaches it is a real trust model, and calling that person a signed-in user
    /// would be this code pretending to know something it does not.
    ///
    /// What may NOT consent is everything that is not a person: the agent, the session
    /// process, and the repo's own file. A checkout that could approve itself is a checkout
    /// nobody is deciding about, which is the whole reason it is a separate principal.
    let approve (actor: ActorRef) (repo: RepoRef) (granted: string list) : Async<Result<unit, string>> =
        async {
            match actor with
            | UserRef _
            | PeerRef _ ->
                match repos () with
                | None -> return Error "this session has no repos"
                | Some service ->
                    match! service.ListRepos () with
                    | Error reason -> return Error reason
                    | Ok listings ->
                        let declared, _ =
                            RepoConfig.readAll reposDir (listings |> List.map (fun listing -> listing.Repo))
                        match askedBy declared repo with
                        | None -> return Error (sprintf "%s asks for nothing this session can resolve" (RepoRef.value repo))
                        // The set that arrived is the set a person read. If it is not what
                        // the repo asks for now, the file moved under them and the decision
                        // they made is not the one they would make.
                        | Some asked when asked <> granted ->
                            return
                                Error (
                                    sprintf
                                        "what %s asks for changed since you looked — it now asks for %s"
                                        (RepoRef.value repo)
                                        (String.concat "; " asked))
                        | Some asked ->
                            do!
                                append
                                    actor
                                    (SessionEvent.RepoCapabilitiesApproved
                                        { RepoCapabilitiesApproved.MessageId = mintMessageId ()
                                          RepoCapabilitiesApproved.Repo = repo
                                          RepoCapabilitiesApproved.Granted = asked
                                          RepoCapabilitiesApproved.Actor = actor })
                            // And then DO it. Consent that leaves the sandbox down until
                            // somebody happens to touch a repo is a button that reports
                            // success and changes nothing — which is the same class of fault
                            // as a refusal nobody sees, arriving from the other direction.
                            //
                            // Safe to call here because the fold is idempotent by
                            // construction: it re-asks for what is already running and
                            // records nothing when nothing changed. It runs on the authority
                            // of whoever approved, which is the truth of why it ran.
                            do! fold (Some actor)
                            return Ok ()
            | _ ->
                return
                    Error
                        "only a person in this session can approve what a repo asks for — an agent cannot consent on a checkout's behalf"
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
      Undeclared = undeclared
      Approve = approve }

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
