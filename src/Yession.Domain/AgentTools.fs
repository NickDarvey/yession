namespace Yession.Domain

// The session's own tools, as a registry (Plan 16, part A).
//
// This is the `yession` namespace: the verbs a turn has always had, moved off the
// runner's parameter list and onto a value. They live in the domain rather than beside the
// SDK adapter for one reason worth stating — what a tool ANSWERS is the interesting part,
// and it is now testable without a model, a subprocess or a browser. The adapter is left
// with the thing only it can do: turning descriptors into SDK tools.
//
// The wire names are unchanged (`mcp__yession__execute_command`, …) because the namespace
// is the SDK server's name and the server was already called `yession`.

open System

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// Reading a call's arguments. Every body does this for itself: the generic path carries
/// JSON precisely so that recording and redaction can be schema-driven, and a decode that
/// lived out there would have to know every tool's shape to do the same job.
module private ToolArgs =

    let private read (decoder: Decoder<'a>) (json: string) : Result<'a, string> =
        let json = if String.IsNullOrWhiteSpace json then "{}" else json
        match Decode.fromString decoder json with
        | Ok value -> Ok value
        | Error e -> Error (sprintf "could not read the arguments: %s" e)

    let string (key: string) (json: string) : Result<string, string> =
        read (Decode.object (fun get -> get.Required.Field key Decode.string)) json

    /// `execute_command`'s pair: the line to run, and which named sandbox to run it in.
    /// An absent or empty `sandbox` is the default one, which is what the optional
    /// parameter degrades to.
    let commandSandbox (json: string) : Result<string * string, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "command" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.defaultValue ""))
            json

    /// `start_work_sandbox`'s pair: the sandbox name, and the credentials to forward.
    let nameForward (json: string) : Result<string * string list, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "forward" (Decode.list Decode.string) |> Option.defaultValue []))
            json

    let two (first: string) (second: string) (json: string) : Result<string * string, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field first Decode.string, get.Required.Field second Decode.string))
            json

    let repoBranchCreate (json: string) : Result<string * string * bool, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "repo" Decode.string,
                get.Required.Field "branch" Decode.string,
                get.Optional.Field "create" Decode.bool |> Option.defaultValue false))
            json

module AgentTools =

    /// The namespace the session's own tools live in. One string, referenced everywhere,
    /// because it is simultaneously the SDK server name, the wire-name segment and what the
    /// tool-use record shows.
    [<Literal>]
    let Namespace = "yession"

    /// One outcome, rendered as tool text (Plan 13, stage 3b).
    ///
    /// Every case says WHICH state it is in, and the wording is load-bearing rather than
    /// decorative: telling a model "queued" when its command is actually blocked on a person
    /// has it conclude, after a silent pause, that the command failed and try something else
    /// — which is exactly how a review gate turns into the agent routing around the review.
    let renderOutcome (outcome: TerminalCommandOutcome) : string =
        let where = sprintf "terminal %s" (TerminalId.value outcome.Terminal)
        let handle = QueueId.value outcome.Handle
        let output =
            if outcome.OutputTail = "" then ""
            else if outcome.Elided > 0 then
                sprintf "\n[%d earlier characters omitted]\n%s" outcome.Elided outcome.OutputTail
            else "\n" + outcome.OutputTail
        match outcome.Status with
        | TerminalCommandRan (CommandSucceeded code) -> sprintf "exit code %d in %s%s" code where output
        | TerminalCommandRan (CommandFailed code) -> sprintf "FAILED with exit code %d in %s%s" code where output
        | TerminalCommandRan CommandTimedOut -> sprintf "TIMED OUT in %s%s" where output
        | TerminalCommandRan (CommandExecutionFailed reason) ->
            sprintf "EXECUTION FAILED in %s: %s%s" where reason output
        | TerminalCommandRunning ->
            sprintf
                "STILL RUNNING in %s. It has NOT finished; nothing was cancelled. Call check_pending with handle '%s' to pick it up.%s"
                where handle output
        | TerminalCommandAwaitingApproval ->
            sprintf
                "WAITING FOR A HUMAN TO APPROVE IT in %s. It has not run. Do not wait for output — say what you queued and why. Call check_pending with handle '%s' once they have had a chance to look."
                where handle
        | TerminalCommandAwaitingTerminal ->
            sprintf
                "WAITING FOR %s TO BE FREE — somebody is using it, or another command is running there. It has not run. Call check_pending with handle '%s' later."
                where handle
        | TerminalCommandRefused (by, reason) ->
            let who = ActorRef.token by
            match reason with
            | Some reason -> sprintf "REFUSED by %s in %s: %s. It did not run — do not try to run it another way." who where reason
            | None -> sprintf "REFUSED by %s in %s. It did not run — do not try to run it another way." who where

    // ---------------------------------------------------------------------------------
    // The bodies. Each takes the raw arguments and answers text. A tool that RAN and went
    // badly answers `Ok` with text saying so — the model is meant to read it and choose
    // differently. `Error` means the call did not happen (arguments unreadable), and only
    // the dispatch below can produce one.
    // ---------------------------------------------------------------------------------

    /// A body that always answers: `Error` is for the call not happening, and once the
    /// arguments have been read it always does.
    let private ok (body: Async<string>) : Async<Result<ToolAnswer, string>> =
        async {
            let! text = body
            return Ok (ToolAnswer.text text)
        }

    /// The same, for the two verbs that can say which BLOCK they became.
    let private answered (body: Async<ToolAnswer>) : Async<Result<ToolAnswer, string>> =
        async {
            let! answer = body
            return Ok answer
        }

    let private withRepo (raw: string) (inner: RepoRef -> Async<string>) : Async<string> =
        async {
            match RepoRef.create raw with
            | Error e -> return sprintf "not a repo name: %s" e
            | Ok repo -> return! inner repo
        }

    let private withSandbox (raw: string) (inner: SandboxName -> Async<string>) : Async<string> =
        async {
            match SandboxName.create raw with
            | Error e -> return sprintf "not a sandbox name: %s" e
            | Ok name -> return! inner name
        }

    // The two verbs that produce a BLOCK say so, because the tool-use record needs to know
    // — a call that became a block draws no chip of its own, and only the call can tell.
    let private executeCommand (capabilities: AgentCapabilities) (command: string) (sandbox: string) : Async<ToolAnswer> =
        async {
            let target =
                if sandbox = "" then Ok None
                else SandboxName.create sandbox |> Result.map (InSandbox >> Some)
            match target with
            | Error e -> return ToolAnswer.text (sprintf "not a sandbox name: %s" e)
            | Ok target ->
                match! capabilities.ExecuteCommand target command with
                | Ok outcome -> return { Text = renderOutcome outcome; Block = outcome.Block }
                | Error reason -> return ToolAnswer.text (sprintf "could not run the command: %s" reason)
        }

    /// The one renderer for every gated command (Plan 15, stage 3b). A gate has three
    /// answers, so this is where they become the three sentences the model reads — once,
    /// rather than once per command, which is what stops a new command inventing its own
    /// vocabulary for "somebody has to approve this".
    ///
    /// A refusal is phrased as a refusal and not as an error the model should route around:
    /// it names who said no, and their reason when they gave one — a decision that reads as
    /// a malfunction gets retried another way.
    let renderCommandOutcome (outcome: CommandOutcome) : string =
        match outcome.Status with
        | CommandRan said -> said
        | CommandAwaitingApproval ->
            match outcome.Handle with
            | Some handle ->
                sprintf
                    "WAITING FOR A HUMAN TO APPROVE `%s`. It has NOT happened. Do not wait for it — say what you proposed and why. Call check_pending with handle '%s' once they have had a chance to look."
                    outcome.Summary
                    (QueueId.value handle)
            | None -> sprintf "WAITING FOR A HUMAN TO APPROVE `%s`. It has NOT happened." outcome.Summary
        | CommandRefusedBy (by, reason) ->
            let who = ActorRef.token by
            match reason with
            | Some reason ->
                sprintf "REFUSED by %s: %s. `%s` did not happen — do not try another way round it." who reason outcome.Summary
            | None -> sprintf "REFUSED by %s. `%s` did not happen — do not try another way round it." who outcome.Summary

    /// `check_pending`: resume a handle, whichever kind of act it named (Plan 15, stage 3b).
    /// ONE verb, because the handle is one type — the alternative is an agent that has to
    /// know, before it asks, what it is waiting on.
    let private checkPending (capabilities: AgentCapabilities) (handle: string) : Async<ToolAnswer> =
        async {
            match QueueId.create handle with
            | Error e -> return ToolAnswer.text (sprintf "not a command handle: %s" e)
            | Ok handle ->
                match! capabilities.CheckPending handle with
                | Ok (PendingTerminal outcome) -> return { Text = renderOutcome outcome; Block = outcome.Block }
                | Ok (PendingCommand outcome) -> return ToolAnswer.text (renderCommandOutcome outcome)
                | Error reason -> return ToolAnswer.text (sprintf "could not read that command: %s" reason)
        }

    let private setSecret (capabilities: AgentCapabilities) (name: string) (value: string) : Async<string> =
        async {
            match SecretName.create name with
            | Error e -> return sprintf "invalid secret name: %s" e
            | Ok secretName ->
                match! capabilities.SetSecret secretName value with
                | Ok metadata ->
                    return
                        sprintf
                            "stored secret '%s' (updated %s)"
                            (SecretName.value metadata.Id.Name)
                            (metadata.UpdatedAt.ToString "o")
                | Error e -> return sprintf "could not store secret: %s" e
        }

    let private listSecrets (capabilities: AgentCapabilities) () : Async<string> =
        async {
            match! capabilities.ListSecrets () with
            | Error e -> return sprintf "could not list secrets: %s" e
            | Ok [] -> return "no secrets stored for this session"
            | Ok listed ->
                return
                    listed
                    |> List.map (fun m -> sprintf "%s (updated %s)" (SecretName.value m.Id.Name) (m.UpdatedAt.ToString "o"))
                    |> String.concat "\n"
        }

    let private deleteSecret (capabilities: AgentCapabilities) (name: string) : Async<string> =
        async {
            match SecretName.create name with
            | Error e -> return sprintf "invalid secret name: %s" e
            | Ok secretName ->
                match! capabilities.DeleteSecret secretName with
                | Ok true -> return sprintf "deleted secret '%s'" name
                | Ok false -> return sprintf "no secret named '%s'" name
                | Error e -> return sprintf "could not delete secret: %s" e
        }

    let private addRepo (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.AddRepo repo with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not add the repo: %s" e
            })

    let private switchBranch (capabilities: AgentCapabilities) (raw: string) (branch: string) (create: bool) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.SwitchRepoBranch repo branch create with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not switch branch: %s" e
            })

    let private fetchRepo (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.FetchRepo repo with
                | Ok said -> return said
                | Error e -> return sprintf "could not fetch: %s" e
            })

    let private inspectRepo (inspect: InspectRepo) (raw: string) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! inspect repo with
                | Ok said -> return said
                | Error e -> return sprintf "could not inspect the repo: %s" e
            })

    /// The named-WorkSandbox bodies (Plan 15, stage 2).
    let private startWorkSandbox (capabilities: AgentCapabilities) (raw: string) (forward: string list) : Async<string> =
        withSandbox raw (fun name ->
            async {
                match! capabilities.StartWorkSandbox name forward with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not start the sandbox: %s" e
            })

    let private stopWorkSandbox (capabilities: AgentCapabilities) (raw: string) : Async<string> =
        withSandbox raw (fun name ->
            async {
                match! capabilities.StopWorkSandbox name with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not stop the sandbox: %s" e
            })

    let private readQuery (capabilities: AgentCapabilities) (def: QueryDef) () : Async<string> =
        async {
            match! capabilities.ReadQuery def.Name with
            | Ok value -> return QueryValue.describe def.Shape value
            | Error e -> return sprintf "could not read %s: %s" (QueryName.value def.Name) e
        }

    // ---------------------------------------------------------------------------------
    // The descriptors, paired with their bodies. One list: a tool cannot be declared
    // without being callable, and cannot be callable without being declared.
    // ---------------------------------------------------------------------------------

    let private repoArg = [ ToolField.required "repo" "string" "owner/name" ]

    /// The verbs: everything that is written out rather than generated.
    let private verbs (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
        let tool name description fields body : ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>) =
            ToolDescriptor.create Namespace name description (ToolSchema.ofFields fields), body
        let ofRepo (run: string -> Async<string>) =
            fun args ->
                async {
                    match ToolArgs.string "repo" args with
                    | Error e -> return Error e
                    | Ok raw -> return! ok (run raw)
                }
        [ tool
            "execute_command"
            "Run a shell command in one of this session's terminals, where the people in the session can see it, edit it, and — depending on the terminal — approve it before it runs. This is the only way to run anything. Pass `sandbox` to run in a named work sandbox (start_work_sandbox creates one); omit it for the default sandbox, which is where everything runs unless you say otherwise. It waits for the result and returns the exit code and output; if it is waiting on a person, or the terminal is busy, or the command is still going, it says so and returns a handle for check_pending instead of hanging. Read what it returns: every answer states which of those happened."
            [ ToolField.required "command" "string" "the shell command line to run, e.g. \"npm test -- --watch=false\""
              ToolField.optional "sandbox" "string" "the work sandbox to run in, e.g. \"test\"; omit for the default one" ]
            (fun args ->
                async {
                    match ToolArgs.commandSandbox args with
                    | Error e -> return Error e
                    | Ok (command, sandbox) -> return! answered (executeCommand capabilities command sandbox)
                })

          tool
              "check_pending"
              "Pick anything back up by the handle it returned — a command waiting on somebody to approve it, an approval that arrived late, a long build. Works for execute_command and for any command that said it was waiting for a human. Returns the same thing the original call would have."
              [ ToolField.required "handle" "string" "the handle from execute_command, or from a command that said it was waiting" ]
              (fun args ->
                  async {
                      match ToolArgs.string "handle" args with
                      | Error e -> return Error e
                      | Ok handle -> return! answered (checkPending capabilities handle)
                  })

          tool
              "set_secret"
              "Persist a named secret for this session (WRITE-ONLY: no tool can read it back). To USE it, reference its name as an environment variable secret ref when an environment starts — the value is injected there directly and never appears in the conversation."
              [ ToolField.required "name" "string" "the secret name, e.g. DEPLOY_TOKEN"
                // The one argument in the repo that must never be recorded, and it says so
                // in the schema rather than in a list somebody has to remember to update.
                ToolField.secret "value" "the secret value to store" ]
              (fun args ->
                  async {
                      match ToolArgs.two "name" "value" args with
                      | Error e -> return Error e
                      | Ok (name, value) -> return! ok (setSecret capabilities name value)
                  })

          tool
              "list_secrets"
              "List the names and timestamps of this session's stored secrets. Never returns values."
              []
              (fun _ -> ok (listSecrets capabilities ()))

          tool
              "delete_secret"
              "Delete one of this session's stored secrets by name."
              [ ToolField.required "name" "string" "the secret name to delete" ]
              (fun args ->
                  async {
                      match ToolArgs.string "name" args with
                      | Error e -> return Error e
                      | Ok name -> return! ok (deleteSecret capabilities name)
                  })

          tool
              "add_repo"
              "Clone a GitHub repo into this session's shared repos directory (visible to everyone here, and inside the work environment). Takes owner/repo — never a URL — and only repos the session's GitHub credential can reach. Read-only bootstrap: to commit or push, use execute_command in a terminal. Already-added repos just report their current state."
              [ ToolField.required "repo" "string" "the repo as owner/name, e.g. \"octocat/hello-world\"" ]
              (ofRepo (addRepo capabilities))

          tool
              "switch_branch"
              "Switch a repo's checkout to a branch (optionally creating it). Local only — never touches the remote. Everyone in the session sees the switch in the timeline."
              [ ToolField.required "repo" "string" "owner/name"
                ToolField.required "branch" "string" "the branch to switch to"
                ToolField.optional "create" "boolean" "create the branch (like switch -c)" ]
              (fun args ->
                  async {
                      match ToolArgs.repoBranchCreate args with
                      | Error e -> return Error e
                      | Ok (repo, branch, create) -> return! ok (switchBranch capabilities repo branch create)
                  })

          tool
              "fetch_repo"
              "Fetch a repo's remote refs (prune, no submodules). Use before switching to a branch that only exists on the remote."
              repoArg
              (ofRepo (fetchRepo capabilities))

          tool "repo_status" "A repo checkout's git status (porcelain, with branch header)." repoArg
              (ofRepo (inspectRepo capabilities.RepoStatus))

          tool "repo_log" "The last 30 commits of a repo checkout, one line each." repoArg
              (ofRepo (inspectRepo capabilities.RepoLog))

          tool "repo_diff" "The uncommitted diff of a repo checkout (capped; use a terminal for the full thing)." repoArg
              (ofRepo (inspectRepo capabilities.RepoDiff))

          tool
              "start_work_sandbox"
              "Make sure a named work sandbox exists for this session, and get it back. Asking twice for the same name with the same forwarding returns the one already running and changes nothing — safe to call every time. Asking for the same name with DIFFERENT forwarding is refused rather than silently recreated, because recreating kills whatever is running inside it: stop_work_sandbox first. `forward` names credentials to put inside the sandbox (currently \"github\", which is what lets git push work from a terminal there); it uses the credentials of the person whose turn this is, and everyone in the session sees which were forwarded and whose."
              [ ToolField.required "name" "string" "the sandbox name, e.g. \"default\" or \"test\""
                ToolField.optionalList "forward" "string" "credential names to forward, e.g. [\"github\"]" ]
              (fun args ->
                  async {
                      match ToolArgs.nameForward args with
                      | Error e -> return Error e
                      | Ok (name, forward) -> return! ok (startWorkSandbox capabilities name forward)
                  })

          tool
              "stop_work_sandbox"
              "Stop a named work sandbox, killing anything running in it. The way to change what a sandbox forwards: stop it, then start it again."
              [ ToolField.required "name" "string" "the sandbox name" ]
              (fun args ->
                  async {
                      match ToolArgs.string "name" args with
                      | Error e -> return Error e
                      | Ok name -> return! ok (stopWorkSandbox capabilities name)
                  }) ]

    /// The session's QUERIES (Plan 15), generated from the registry rather than written out
    /// one by one: declaring a query is what puts it in front of the agent, and in front of
    /// the humans, with no third place to keep in step. `readOnlyHint` is what makes one a
    /// query rather than a command, and it is MCP's own marker, not ours.
    let private queryTools (capabilities: AgentCapabilities) : (ToolDescriptor * (string -> Async<Result<ToolAnswer, string>>)) list =
        capabilities.Queries
        |> List.map (fun def ->
            { ToolDescriptor.create Namespace (QueryName.value def.Name) (QueryDef.toolDescription def) ToolSchema.none with
                ReadOnly = true
                Title = Some def.Title },
            (fun _ -> ok (readQuery capabilities def ())))

    /// Every tool of the `yession` namespace: its descriptor, and the body that answers a
    /// call to it. One list — a tool cannot be declared without being callable, and cannot
    /// be callable without being declared.
    let private declared (capabilities: AgentCapabilities) = verbs capabilities @ queryTools capabilities

    /// The `yession` registry for one turn's capabilities.
    let registry (capabilities: AgentCapabilities) : ToolRegistry =
        let declared = declared capabilities
        ToolRegistry.ofNamespace
            Namespace
            (declared |> List.map fst)
            (fun call ->
                match declared |> List.tryFind (fun (descriptor, _) -> descriptor.Name = call.Name) with
                | Some (_, body) -> body call.Arguments
                | None -> async { return Error (sprintf "no tool '%s/%s'" call.Namespace call.Name) })
