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

open Yession.Domain
open Yession.SessionProcess

/// What the commands need from the session around them, as getters — the services are
/// composed during boot and the table is built once, so a value read here would be the one
/// that existed before the session did.
type CommandServices =
    { /// The repo manager, absent when the session could not start one.
      Repos : unit -> Repos.ReposService option
      /// The session's named WorkSandboxes.
      Sandboxes : unit -> WorkSandboxes.WorkSandboxes
      /// Say a query's answer changed. A command is the only thing that can change one, so
      /// a command is the only thing that has to say so — nothing polls.
      Invalidate : QueryName -> unit }

let private encodeArgs (values: string list) : string = Codec.toString Codec.gatedArgs values

let private decodeArgs (raw: string) : string list =
    match Codec.fromString Codec.gatedArgs raw with
    | Ok values -> values
    | Error _ -> []

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
let private switchBranchTool = "switch_branch"
let private startWorkSandboxTool = "start_work_sandbox"
let private stopWorkSandboxTool = "stop_work_sandbox"

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
        Repos.agentCaller (Authority.effective invocation.Authority) (Authority.approver invocation.Authority)
    let sandboxCaller (invocation: GatedInvocation) : WorkSandboxes.SandboxCaller =
        { Actor = Authority.author invocation.Authority
          Credential = Authority.effective invocation.Authority
          ApprovedBy = Authority.approver invocation.Authority }
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
                            andPublish services Repos.queryName (
                                async {
                                    match! service.AddRepo (repoCaller invocation) repo with
                                    | Error e -> return Error e
                                    | Ok listing ->
                                        return
                                            Ok (
                                                sprintf
                                                    "added %s — the checkout is shared with everyone in this session and visible in the work environment"
                                                    (RepoListing.describe listing))
                                })
                | Some _, other -> return Error (sprintf "add_repo takes one repo, got %d arguments" (List.length other))
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
                            andPublish services Repos.queryName (
                                async {
                                    match! service.SwitchBranch (repoCaller invocation) repo branch (create = "true") with
                                    | Error e -> return Error e
                                    | Ok listing -> return Ok (sprintf "now on %s" (RepoListing.describe listing))
                                })
                | Some _, other ->
                    return Error (sprintf "switch_branch takes a repo, a branch and a flag, got %d arguments" (List.length other))
            }

          startWorkSandboxTool,
          fun (invocation: GatedInvocation) ->
            async {
                match decodeArgs invocation.Args with
                | name :: forward ->
                    match SandboxName.create name with
                    | Error e -> return Error (sprintf "not a sandbox name: %s" e)
                    | Ok name ->
                        match! (services.Sandboxes ()).Ensure (sandboxCaller invocation) name forward with
                        | Error e -> return Error e
                        | Ok entry ->
                            services.Invalidate WorkSandboxes.queryName
                            let forwarding =
                                match entry.Forwarded with
                                | [] -> "nothing forwarded into it"
                                | names -> "forwarding " + String.concat ", " names
                            return
                                Ok (
                                    sprintf
                                        "sandbox '%s' is up on %s, %s — run things in it with execute_command"
                                        (SandboxName.value entry.Name)
                                        entry.Backend
                                        forwarding)
                | [] -> return Error "start_work_sandbox takes a sandbox name"
            }

          stopWorkSandboxTool,
          fun (invocation: GatedInvocation) ->
            async {
                match decodeArgs invocation.Args with
                | [ name ] ->
                    match SandboxName.create name with
                    | Error e -> return Error (sprintf "not a sandbox name: %s" e)
                    | Ok name ->
                        match! (services.Sandboxes ()).Stop (sandboxCaller invocation) name with
                        | Error e -> return Error e
                        | Ok () ->
                            services.Invalidate WorkSandboxes.queryName
                            return Ok (sprintf "sandbox '%s' is stopped; anything running in it is gone" (SandboxName.value name))
                | other -> return Error (sprintf "stop_work_sandbox takes one sandbox name, got %d arguments" (List.length other))
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
            AddRepo =
              fun repo ->
                gated addRepoTool [ RepoRef.value repo ] (sprintf "add_repo %s" (RepoRef.value repo))
            SwitchRepoBranch =
              fun repo branch create ->
                let summary =
                    if create then sprintf "switch_branch %s -> new branch %s" (RepoRef.value repo) branch
                    else sprintf "switch_branch %s -> %s" (RepoRef.value repo) branch
                gated switchBranchTool [ RepoRef.value repo; branch; (if create then "true" else "false") ] summary
            // The READS take no gate and no approver: they change nothing, so there is
            // nothing to approve and nothing to resume.
            FetchRepo = service.FetchRepo (Repos.agentCaller turnActor None)
            RepoStatus = service.RepoStatus
            RepoLog = service.RepoLog
            RepoDiff = service.RepoDiff }

/// The turn's sandbox commands (Plan 15, stage 2), bound to the acting party.
let private sandboxCapabilitiesFor (turnActor: ActorRef) (capabilities: AgentCapabilities) : AgentCapabilities =
    let gated (tool: string) (args: string list) (summary: string) =
        capabilities.RunGated
            { Tool = tool
              Args = encodeArgs args
              Summary = summary
              Authority = Authority.agentFor turnActor }
    { capabilities with
        StartWorkSandbox =
          fun name forward ->
            let summary =
                match WorkSandboxes.normaliseForward forward with
                | [] -> sprintf "start_work_sandbox %s" (SandboxName.value name)
                | names ->
                    sprintf "start_work_sandbox %s forwarding %s" (SandboxName.value name) (String.concat ", " names)
            gated startWorkSandboxTool (SandboxName.value name :: forward) summary
        StopWorkSandbox =
          fun name ->
            gated
                stopWorkSandboxTool
                [ SandboxName.value name ]
                (sprintf "stop_work_sandbox %s" (SandboxName.value name)) }

/// Every command verb bound to ONE turn's actor: the acting party on the events is the agent,
/// the credential is the turn human's (Plan 08). The Host leaves these as denials because
/// only the per-turn dispatcher knows who the turn is for; this is where they stop being
/// denials, and it is one call so a turn cannot pick up half of them.
let bindFor (services: CommandServices) (turnActor: ActorRef) (capabilities: AgentCapabilities) : AgentCapabilities =
    capabilities |> repoCapabilitiesFor services turnActor |> sandboxCapabilitiesFor turnActor
