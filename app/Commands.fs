module Yession.Host.Commands

// The session's gated COMMANDS, both halves in one place (Plan 14, Plan 15 stage 2/3b).
//
// A gated command is two functions that only make sense together: the one a turn calls,
// which encodes the arguments and hands them to the gate, and the one the GATE calls once a
// verdict is in, which decodes those same arguments and carries the act out. They are joined
// by nothing but a string — the tool name — and by the encoding of the argument list, so a
// rename or a re-ordering on one side is a runtime failure on the other.
//
// Both used to live in `SessionMain.fs`, which is an entry: it has top-level effects, so
// nothing may reference it and no test can reach what it holds. The pair therefore had the
// shape the colocation rule warns about — a decision taken in the composition root, where the
// cheap tier cannot see it. They are here, beside each other, so a harness can drive a tool
// call against the SAME bindings a turn uses rather than against a re-statement of them.
//
// What stays in the entry is composition: which services exist, and handing the table to the
// gate the Host owns.
//
// Every command here is ENSURE-shaped: re-running one with the same arguments converges on the
// same state, records nothing, and says so. That is not politeness — the declarative form of
// this API is a fold of a config file into these commands on every boot, so a command that
// accumulated instead of converging would clone a repo twice, or restart a sandbox somebody is
// working in, each time the session came up. `add_repo` answers with the current listing when
// the checkout is already there; `start_work_sandbox` hands back the running sandbox and
// refuses only when the configuration differs.

open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Tools
open Yession.Domain.Repos
open Yession.SessionProcess
open Yession.Domain.Prs

/// What the commands need from the session around them, as getters — the services are
/// composed during boot and the table is built once, so a value read here would be the one
/// that existed before the session did.
type CommandServices =
    { /// The repo manager, absent when the session could not start one.
      Repos : unit -> Repos.ReposService option
      /// The session's named WorkSandboxes.
      Sandboxes : unit -> WorkSandboxes.WorkSandboxes
      /// A repo's checkout in both its addresses (`Sandboxes.checkoutViewsAt`) — the
      /// sandbox's own view, which a repo-owned declaration's `workdir:` resolves
      /// against, and the host's, which its `build:` context does. Its own function
      /// rather than a repo verb because it is a different fact: the verbs answer in
      /// the view of the session's own sandboxes, where their answers are acted on,
      /// and a repo's sandbox is a container with a view of its own.
      WorkCheckout : RepoRef -> CheckoutViews
      /// The terminal manager, which owns the shell profile (Plan 25).
      Terminals : unit -> SessionTerminals.SessionTerminals
      /// Queueing a command as a recorded block — the same door `execute_command` goes
      /// through, which is the point: a sandbox's declared `setup:` is a command somebody
      /// can watch, edit before it runs, and read the outcome of afterwards, not a private
      /// spawn this layer arranges on the side.
      RunCommand : unit -> TerminalCommands.TerminalCommands
      /// Watching pull requests, absent when the session could not start the poller.
      Prs : unit -> PrWatches.PrWatchService option
      /// Say a query's answer changed. A command is the only thing that can change one, so
      /// a command is the only thing that has to say so — nothing polls.
      Invalidate : QueryName -> unit
      /// Re-read every checkout's `yession.yaml` and ensure what it declares (Plan 27), on
      /// the authority of whoever ran the verb.
      ///
      /// Called by the three verbs that change what checkouts exist or what is in them, and
      /// by nothing else. It is safe to call whenever one of them succeeds because the fold
      /// is idempotent — a declaration that is already a running sandbox is an ask that
      /// changes nothing and records nothing, which is what every mutating command being
      /// ensure-shaped bought.
      Refold : ActorRef option -> Async<unit> }

let private encodeArgs (values: string list) : string = Codec.toString Codec.gatedArgs values

let private decodeArgs (raw: string) : string list =
    match Codec.fromString Codec.gatedArgs raw with
    | Ok values -> values
    | Error _ -> []

/// Re-read every checkout's `yession.yaml` once a verb has actually changed what checkouts
/// exist or what is in them (Plan 27).
///
/// On SUCCESS only: a verb that refused changed nothing, so a fold behind a refusal would be
/// work nobody asked for. And it wraps rather than being written into each body because the
/// three verbs it applies to have nothing else in common with it — what a fold reads is none
/// of their business, and the day a fourth verb changes a checkout, this is the one thing it
/// has to be given.
let private andRefold
    (services: CommandServices)
    (invocation: GatedInvocation)
    (outcome: Async<Result<'a, string>>)
    : Async<Result<'a, string>> =
    async {
        let! result = outcome
        match result with
        // Whoever the verb ran on the authority of. A `forward:` in a file the fold picks up
        // resolves for THEM, by the same Plan 08 precedence the verb itself used.
        | Ok _ -> do! services.Refold (Some (Authority.effective invocation.Authority))
        | Error _ -> ()
        return result
    }

/// Invalidate a query once a command has actually changed its answer.
let private andPublish
    (services: CommandServices)
    (name: QueryName)
    (outcome: Async<Result<'a, string>>)
    : Async<Result<'a, string>> =
    async {
        let! result = outcome
        match result with
        | Ok _ -> services.Invalidate name
        | Error _ -> ()
        return result
    }

// The gated commands, by MCP tool name. The dispatch table's keys and every call site
// live in this one file, so the name is agreed where it is used — the settings surface and
// boot configuration that used to read a shared catalogue are gone (Plan 23).
let private addRepoTool = "add_repo"
let private removeRepoTool = "remove_repo"
let private switchBranchTool = "switch_branch"
let private watchPrTool = "watch_pr"
let private unwatchPrTool = "unwatch_pr"
let private startWorkSandboxTool = "start_work_sandbox"
let private stopWorkSandboxTool = "stop_work_sandbox"
let private setShellProfileTool = "set_shell_profile"

/// One `start_work_sandbox` call, built where the dispatch entry that reads it lives.
///
/// The encoding is private and stays private: a caller that spelled these arguments itself
/// would be a second spelling of them, and the two would disagree the first time one
/// changed. Every route to this verb — the agent's capability below, the fold over
/// `yession.yaml` — goes through here.
let startWorkSandboxCall (authority: Authority) (sandbox: SandboxRef) (decl: SandboxDecl) : GatedCall =
    { Tool = startWorkSandboxTool
      Args = encodeArgs [ SandboxRef.render sandbox; SandboxDecl.encode decl ]
      Summary =
        match WorkSandboxes.normaliseForward decl.Forward with
        | [] -> sprintf "start_work_sandbox %s" (SandboxRef.render sandbox)
        | names ->
            sprintf "start_work_sandbox %s forwarding %s" (SandboxRef.render sandbox) (String.concat ", " names)
      Authority = authority }

/// How each gated command is actually carried out, by tool name (Plan 15, stage 3b).
///
/// A malformed invocation FAILS rather than guessing: "run something adjacent to what was
/// asked for" is the one outcome a gate must never produce.
let dispatch (services: CommandServices) : CommandDispatch =
    // Whose credential, asked once: the borrowed authority when there is one, the author
    // otherwise. It used to be a `defaultArg` per call site with `ActorRef.Agent` written in
    // as the fallback — which was right only because the agent is what authored every one of
    // these, a coincidence each site had to keep re-establishing.
    let repoCaller (invocation: GatedInvocation) =
        Repos.agentCaller (Authority.effective invocation.Authority)
    let sandboxCaller (invocation: GatedInvocation) : WorkSandboxes.SandboxCaller =
        { Actor = Authority.author invocation.Authority
          Credential = Authority.effective invocation.Authority }
    Map.ofList
        [ addRepoTool,
          fun (invocation: GatedInvocation) ->
            async {
                match services.Repos (), decodeArgs invocation.Args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andRefold services invocation (
                            andPublish services Repos.queryName (
                                async {
                                    match! service.AddRepo (repoCaller invocation) repo with
                                    | Error e -> return Error e
                                    | Ok listing ->
                                        // Said HERE, at the moment a checkout first exists,
                                        // because that is when it is worth acting on. The
                                        // same advice sits on `set_shell_profile`, where only
                                        // an agent already reaching for that tool reads it —
                                        // which is not the agent about to `cd` in front of
                                        // every command for the rest of the session.
                                        //
                                        // Conditioned on there being no profile rather than
                                        // on this being the first repo: the point is that
                                        // terminals still start somewhere else, and it stops
                                        // saying so once somebody has decided where.
                                        let unset =
                                            (services.Terminals ()).Profiles ()
                                            |> ShellProfileProjection.workingDirectory SandboxRef.defaultRef
                                            |> Option.isNone
                                        return
                                            Ok (
                                                sprintf
                                                    "added %s — the checkout is shared with everyone in this session and visible in the work environment%s"
                                                    (RepoListing.describe listing)
                                                    (if unset then
                                                         ". Terminals do not start there: set_shell_profile with that path if this is where the work is."
                                                     else
                                                         ""))
                                }))
                | Some _, other -> return Error (sprintf "add_repo takes one repo, got %d arguments" (List.length other))
            }

          removeRepoTool,
          fun (invocation: GatedInvocation) ->
            async {
                match services.Repos (), decodeArgs invocation.Args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo; force ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andRefold services invocation (
                            andPublish services Repos.queryName (
                                async {
                                    match! service.RemoveRepo (repoCaller invocation) repo (force = "true") with
                                    | Error e -> return Error e
                                    | Ok path ->
                                        // Plan 25's upstream half, with a caller at last: a
                                        // profile pointing inside a tree that has gone would
                                        // send every future terminal somewhere that no longer
                                        // exists. The repo service contributes the one fact
                                        // only it has — the path, as a terminal saw it — and
                                        // the terminal manager decides which profiles that
                                        // invalidates. Nothing is computed here.
                                        let! cleared =
                                            (services.Terminals ())
                                                .ClearProfilesUnder (Authority.author invocation.Authority) path
                                        if not (List.isEmpty cleared) then
                                            services.Invalidate ShellProfile.queryName
                                        let profiles =
                                            match cleared with
                                            | [] -> ""
                                            | names ->
                                                sprintf
                                                    " New terminals in %s start where the sandbox puts them again."
                                                    (names |> List.map SandboxRef.render |> String.concat ", ")
                                        return
                                            Ok (
                                                sprintf
                                                    "removed %s — the checkout is gone from this session, and from the work environment.%s"
                                                    (RepoRef.value repo)
                                                    profiles)
                                }))
                | Some _, other ->
                    return Error (sprintf "remove_repo takes a repo and a flag, got %d arguments" (List.length other))
            }

          switchBranchTool,
          fun (invocation: GatedInvocation) ->
            async {
                match services.Repos (), decodeArgs invocation.Args with
                | None, _ -> return Error "this session has no repos"
                | Some service, [ repo; branch; create ] ->
                    match RepoRef.create repo with
                    | Error e -> return Error (sprintf "not a repo name: %s" e)
                    | Ok repo ->
                        return!
                            andRefold services invocation (
                            andPublish services Repos.queryName (
                                async {
                                    match! service.SwitchBranch (repoCaller invocation) repo branch (create = "true") with
                                    | Error e -> return Error e
                                    | Ok listing -> return Ok (sprintf "now on %s" (RepoListing.describe listing))
                                }))
                | Some _, other ->
                    return Error (sprintf "switch_branch takes a repo, a branch and a flag, got %d arguments" (List.length other))
            }

          watchPrTool,
          fun (invocation: GatedInvocation) ->
            async {
                match services.Prs (), decodeArgs invocation.Args with
                | None, _ -> return Error "this session cannot watch pull requests"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr ->
                            return!
                                andPublish services PrWatches.queryName (
                                    service.Watch
                                        (Authority.author invocation.Authority)
                                        (Authority.effective invocation.Authority)
                                        pr)
                | Some _, other ->
                    return Error (sprintf "watch_pr takes a repo and a number, got %d arguments" (List.length other))
            }

          unwatchPrTool,
          fun (invocation: GatedInvocation) ->
            async {
                match services.Prs (), decodeArgs invocation.Args with
                | None, _ -> return Error "this session cannot watch pull requests"
                | Some service, [ repo; number ] ->
                    match RepoRef.create repo, System.Int32.TryParse number with
                    | Error e, _ -> return Error (sprintf "not a repo name: %s" e)
                    | _, (false, _) -> return Error "not a pull request number"
                    | Ok repo, (true, number) ->
                        match PrRef.create repo number with
                        | Error e -> return Error e
                        | Ok pr ->
                            return!
                                andPublish services PrWatches.queryName (
                                    service.Unwatch (Authority.author invocation.Authority) pr)
                | Some _, other ->
                    return Error (sprintf "unwatch_pr takes a repo and a number, got %d arguments" (List.length other))
            }

          startWorkSandboxTool,
          fun (invocation: GatedInvocation) ->
            async {
                // A sandbox and a DECLARATION, because a declaration is what both callers
                // have: the agent's names some credentials, a repo's file names everything.
                // One shape, so the declarative route and the interactive one cannot
                // diverge — which is the reason this gate is a capability at all.
                match decodeArgs invocation.Args with
                | [ name; declared ] ->
                    match SandboxRef.parse name with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok name ->
                        match ConfigFile.parseSandbox declared with
                        | Error e -> return Error (sprintf "not a sandbox declaration: %s" e)
                        | Ok decl ->
                            // The checkout is DERIVED from the sandbox's scope, never
                            // carried: a gated call that could name a checkout could name
                            // any directory on this host. And it is the checkout as the
                            // SANDBOX will see it — a repo's sandbox is a container, so
                            // its `workdir:` resolves against the container's view of the
                            // checkout, not the path a terminal in `default` would use.
                            let checkout =
                                match SandboxRef.scope name, services.Repos () with
                                | RepoOwned repo, Some _ -> Some (services.WorkCheckout repo)
                                | RepoOwned _, None
                                | SessionOwned, _ -> None
                            match SandboxDecl.toRequest checkout decl with
                            | Error e -> return Error e
                            | Ok request ->
                                match! (services.Sandboxes ()).Ensure (sandboxCaller invocation) name request with
                                | Error e -> return Error e
                                | Ok outcome ->
                                    let entry = WorkSandboxes.SandboxOutcome.sandbox outcome
                                    services.Invalidate WorkSandboxes.queryName
                                    // What the repo declared to make this sandbox ready,
                                    // queued as a block anyone can watch — the same door
                                    // `execute_command` goes through, so it is on the record
                                    // and its failure is readable where every other
                                    // command's is.
                                    //
                                    // Only for a sandbox this ask STARTED. The fold re-asks
                                    // at boot and after every repo verb, and a setup block
                                    // appearing each time somebody touched a checkout is
                                    // noise nobody asked for.
                                    //
                                    // BACKGROUND, and not awaited: a setup worth declaring
                                    // is the slow thing the first command would otherwise
                                    // pay for, and blocking here would move that cost onto
                                    // the boot fold instead. The terminal serialises, so the
                                    // agent's next command in this sandbox queues behind it
                                    // without anyone arranging that.
                                    let! setup =
                                        match entry.Request.Spec.Setup, outcome with
                                        | Some command, WorkSandboxes.SandboxStarted _ ->
                                            async {
                                                match!
                                                    (services.RunCommand ()).Execute
                                                        { CommandRequest.ofCommand command with
                                                            Target = Some (InSandbox entry.Ref)
                                                            Background = true }
                                                        invocation.Authority
                                                    with
                                                | Ok _ -> return " — running its setup"
                                                // Said, never fatal: the sandbox is up, and
                                                // a setup that could not be QUEUED is worth
                                                // reading rather than a start that reports
                                                // failure for something already running.
                                                | Error reason ->
                                                    return sprintf " — its setup could not be queued: %s" reason
                                            }
                                        | _ -> async { return "" }
                                    let forwarding =
                                        match entry.Request.Forward with
                                        | [] -> "nothing forwarded into it"
                                        | names -> "forwarding " + String.concat ", " names
                                    return
                                        Ok (
                                            sprintf
                                                "sandbox '%s' is up on %s, %s — run things in it with execute_command%s"
                                                (SandboxRef.render entry.Ref)
                                                entry.Backend
                                                forwarding
                                                setup)
                | other ->
                    return
                        Error (
                            sprintf
                                "start_work_sandbox takes a sandbox and a declaration, got %d arguments"
                                (List.length other))
            }

          stopWorkSandboxTool,
          fun (invocation: GatedInvocation) ->
            async {
                match decodeArgs invocation.Args with
                | [ name ] ->
                    match SandboxRef.parse name with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok name ->
                        match! (services.Sandboxes ()).Stop (sandboxCaller invocation) name with
                        | Error e -> return Error e
                        | Ok () ->
                            services.Invalidate WorkSandboxes.queryName
                            return Ok (sprintf "sandbox '%s' is stopped; anything running in it is gone" (SandboxRef.render name))
                | other -> return Error (sprintf "stop_work_sandbox takes one sandbox name, got %d arguments" (List.length other))
            }

          setShellProfileTool,
          fun (invocation: GatedInvocation) ->
            async {
                // A directory is present or it is not, and the ARITY carries that — one arg
                // clears the profile, two sets it. The absence is a shorter list, never an
                // empty string standing in for a value; both halves of this gated command
                // live in one file so the encode above and this decode stay the one shape.
                let parsed =
                    match decodeArgs invocation.Args with
                    | [ name ] -> Ok (name, None)
                    | [ name; cwd ] -> Ok (name, Some cwd)
                    | other -> Error other
                match parsed with
                | Error other ->
                    return
                        Error (
                            sprintf
                                "set_shell_profile takes a sandbox name and an optional directory, got %d arguments"
                                (List.length other))
                | Ok (rawName, cwd) ->
                    match SandboxRef.parse rawName with
                    | Error e -> return Error (sprintf "not a sandbox: %s" e)
                    | Ok name ->
                        return!
                            andPublish services ShellProfile.queryName (
                                (services.Terminals ()).SetProfile (Authority.author invocation.Authority) name cwd)
            } ]

/// The turn's repo verbs (Plan 14), bound to the acting party. The MUTATING ones are three
/// lines each: encode the arguments, render the summary, hand both to the gate. What they
/// used to do lives in `dispatch` above, where a process that did not propose the act can
/// still reach it.
let private repoCapabilitiesFor
    (services: CommandServices)
    (turnActor: ActorRef)
    (capabilities: AgentCapabilities)
    : AgentCapabilities =
    match services.Repos () with
    | None -> capabilities
    | Some service ->
        let gated (tool: string) (args: string list) (summary: string) =
            capabilities.RunGated
                { Tool = tool
                  Args = encodeArgs args
                  Summary = summary
                  // The agent acts; the credential is the turn human's (Plan 08). `agentFor`
                  // TAKES that actor, so an agent-authored call with nobody's authority on it
                  // is not something this could be written to omit.
                  Authority = Authority.agentFor turnActor }
        { capabilities with
            Repos =
              { capabilities.Repos with
                  Add =
                    fun repo ->
                      gated addRepoTool [ RepoRef.value repo ] (sprintf "add_repo %s" (RepoRef.value repo))
                  Remove =
                    fun repo force ->
                      // The cost is in the SUMMARY, because that is the sentence the classifier
                      // reads and a person watching the queue sees BEFORE it happens rather than
                      // after. A removal that would take uncommitted work with it must not look
                      // like one that would not.
                      let summary =
                          if force then sprintf "remove_repo %s (deleting uncommitted changes)" (RepoRef.value repo)
                          else sprintf "remove_repo %s" (RepoRef.value repo)
                      gated removeRepoTool [ RepoRef.value repo; (if force then "true" else "false") ] summary
                  SwitchBranch =
                    fun repo branch create ->
                      let summary =
                          if create then sprintf "switch_branch %s -> new branch %s" (RepoRef.value repo) branch
                          else sprintf "switch_branch %s -> %s" (RepoRef.value repo) branch
                      gated switchBranchTool [ RepoRef.value repo; branch; (if create then "true" else "false") ] summary
                  WatchPr =
                    fun repo number ->
                      gated
                          watchPrTool
                          [ RepoRef.value repo; string number ]
                          (sprintf "watch_pr %s#%d" (RepoRef.value repo) number)
                  UnwatchPr =
                    fun repo number ->
                      gated
                          unwatchPrTool
                          [ RepoRef.value repo; string number ]
                          (sprintf "unwatch_pr %s#%d" (RepoRef.value repo) number)
                  // The READS take no gate and no approver: they change nothing, so there is
                  // nothing to approve and nothing to resume.
                  Fetch = service.FetchRepo (Repos.agentCaller turnActor)
                  Status = service.RepoStatus
                  Log = service.RepoLog
                  Diff = service.RepoDiff } }

/// The turn's sandbox commands (Plan 15, stage 2) and the shell profile (Plan 25), bound to
/// the acting party.
let private sandboxCapabilitiesFor (turnActor: ActorRef) (capabilities: AgentCapabilities) : AgentCapabilities =
    let gated (tool: string) (args: string list) (summary: string) =
        capabilities.RunGated
            { Tool = tool
              Args = encodeArgs args
              Summary = summary
              Authority = Authority.agentFor turnActor }
    { capabilities with
        Sandboxes =
          { capabilities.Sandboxes with
              Start =
                fun name decl -> capabilities.RunGated (startWorkSandboxCall (Authority.agentFor turnActor) name decl)
              Stop =
                fun name ->
                  gated
                      stopWorkSandboxTool
                      [ SandboxRef.render name ]
                      (sprintf "stop_work_sandbox %s" (SandboxRef.render name))
              SetShellProfile =
                fun name cwd ->
                  let summary =
                      match cwd with
                      | Some cwd -> sprintf "set_shell_profile %s -> %s" (SandboxRef.render name) cwd
                      | None -> sprintf "set_shell_profile %s -> wherever the sandbox puts them" (SandboxRef.render name)
                  gated setShellProfileTool (SandboxRef.render name :: Option.toList cwd) summary } }

/// Every command verb bound to ONE turn's actor: the acting party on the events is the agent,
/// the credential is the turn human's (Plan 08). The Host leaves these as denials because
/// only the per-turn dispatcher knows who the turn is for; this is where they stop being
/// denials, and it is one call so a turn cannot pick up half of them.
let bindFor (services: CommandServices) (turnActor: ActorRef) (capabilities: AgentCapabilities) : AgentCapabilities =
    capabilities |> repoCapabilitiesFor services turnActor |> sandboxCapabilitiesFor turnActor
