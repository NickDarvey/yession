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
open Yession.Domain.Terminals
open Yession.Domain.Tools

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

    /// `execute_command`'s three: the line, which named sandbox to run it in, and whether the
    /// caller intends to wait for it (Plan 20, stage 2). An absent or empty `sandbox` is the
    /// default one, which is what the optional parameter degrades to.
    let commandSandbox (json: string) : Result<string * string * bool, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "command" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.defaultValue "",
                get.Optional.Field "background" Decode.bool |> Option.defaultValue false))
            json

    /// `start_work_sandbox`'s pair: the sandbox name, and the credentials to forward.
    /// `open_terminal`'s pair: what the terminal is for, and which sandbox to open it in.
    let nameSandbox (json: string) : Result<string * string, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.defaultValue ""))
            json

    let nameForward (json: string) : Result<string * string list, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "name" Decode.string,
                get.Optional.Field "forward" (Decode.list Decode.string) |> Option.defaultValue []))
            json

    /// `set_shell_profile`'s pair, both optional: where shells opened from now on start,
    /// and whose sandbox. An absent `cwd` is the CLEAR, and an absent `sandbox` is the
    /// default one — so a bare call means "put the default sandbox back the way it was".
    let cwdSandbox (json: string) : Result<string option * string, string> =
        read
            (Decode.object (fun get ->
                get.Optional.Field "cwd" Decode.string,
                get.Optional.Field "sandbox" Decode.string |> Option.defaultValue ""))
            json

    let two (first: string) (second: string) (json: string) : Result<string * string, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field first Decode.string, get.Required.Field second Decode.string))
            json

    /// `read_terminal`'s three: which terminal, where to read from, and what to wait for. An
    /// absent `from` is the tail and an absent `wait_for` is a read that does not wait, which
    /// is what the optional parameters degrade to. A `timeout_seconds` without a `wait_for` is
    /// nothing to bound, so the wait is only assembled when there is something to wait FOR.
    let terminalRead (json: string) : Result<string * int option * TerminalWait option, string> =
        let decoded =
            read
                (Decode.object (fun get ->
                    get.Required.Field "terminal" Decode.string,
                    get.Optional.Field "from" Decode.int,
                    get.Optional.Field "wait_for" Decode.string |> Option.filter (fun s -> s <> ""),
                    get.Optional.Field "wait_for_pattern" Decode.string |> Option.filter (fun s -> s <> ""),
                    get.Optional.Field "timeout_seconds" Decode.float |> Option.defaultValue 10.0))
                json
        match decoded with
        | Error e -> Error e
        | Ok (terminal, from, literal, pattern, timeout) ->
            // Two fields rather than one and a mode flag, because a flag makes the meaning of
            // `wait_for` depend on another argument: a caller that sets one and forgets the
            // other gets a LITERAL match on a regex string, which is wrong, silent, and looks
            // exactly like a device that never answered. Two fields make that a refusal.
            match literal, pattern with
            | Some _, Some _ ->
                Error "wait_for and wait_for_pattern are two ways to say the same thing — give one"
            | None, None -> Ok (terminal, from, None)
            | Some literal, None ->
                Ok (terminal, from, Some { Until = MatchLiteral literal; TimeoutSeconds = timeout })
            | None, Some source ->
                // Compiled HERE, so a pattern outside the subset is an answer to this call
                // rather than something discovered part-way through a wait that then has to
                // explain itself.
                TerminalPattern.compile source
                |> Result.map (fun compiled ->
                    terminal, from, Some { Until = MatchPattern (compiled, source); TimeoutSeconds = timeout })

    /// `remove_repo`'s pair: which repo, and whether uncommitted changes may go with it.
    let repoForce (json: string) : Result<string * bool, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "repo" Decode.string,
                get.Optional.Field "force" Decode.bool |> Option.defaultValue false))
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
        | TerminalCommandInteractive ->
            sprintf
                "WAITING FOR A KEYSTROKE in %s. It opened a full-screen program, so it will not finish on its own — and the terminal is now YOURS to type into. Use write_terminal to answer it and read_terminal to see the screen; leaving the program hands the terminal back and finishes the command. Call check_pending with handle '%s' for the outcome.%s"
                where handle output
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

    let private withSandbox (raw: string) (inner: SandboxRef -> Async<string>) : Async<string> =
        async {
            match SandboxRef.parse raw with
            | Error e -> return sprintf "not a sandbox: %s" e
            | Ok name -> return! inner name
        }

    // The two verbs that produce a BLOCK say so, because the tool-use record needs to know
    // — a call that became a block draws no chip of its own, and only the call can tell.
    let private executeCommand
        (capabilities: AgentCapabilities)
        (command: string)
        (sandbox: string)
        (background: bool)
        : Async<ToolAnswer> =
        async {
            let target =
                if sandbox = "" then Ok None
                else SandboxRef.parse sandbox |> Result.map (InSandbox >> Some)
            match target with
            | Error e -> return ToolAnswer.text (sprintf "not a sandbox: %s" e)
            | Ok target ->
                match! capabilities.ExecuteCommand { Command = command; Target = target; Background = background } with
                | Ok outcome -> return { Text = renderOutcome outcome; Block = outcome.Block; Stream = None }
                | Error reason -> return ToolAnswer.text (sprintf "could not run the command: %s" reason)
        }

    /// The one renderer for every gated command (Plan 15, stage 3b). A gate has few
    /// answers, so this is where they become the sentences the model reads — once, rather
    /// than once per command, which is what stops a new command inventing its own
    /// vocabulary for "this was refused".
    ///
    /// A refusal is phrased as a refusal and not as an error the model should route around:
    /// it names who said no, and their reason when they gave one — a decision that reads as
    /// a malfunction gets retried another way.
    let renderCommandOutcome (outcome: CommandOutcome) : string =
        match outcome.Status with
        | CommandRan said -> said
        // Nobody is being waited for here, and saying otherwise is the failure this case
        // exists to end: a slow command is work in progress, not a decision somebody was
        // never offered.
        | CommandRunning ->
            match outcome.Handle with
            | Some handle ->
                sprintf
                    "STILL RUNNING: `%s` has not finished. Nothing was cancelled and nobody is waiting on you. Call check_pending with handle '%s' to pick it up."
                    outcome.Summary
                    (QueueId.value handle)
            | None -> sprintf "STILL RUNNING: `%s` has not finished. Nothing was cancelled." outcome.Summary
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
                | Ok (PendingTerminal outcome) -> return { Text = renderOutcome outcome; Block = outcome.Block; Stream = None }
                | Ok (PendingCommand outcome) -> return ToolAnswer.text (renderCommandOutcome outcome)
                | Error reason -> return ToolAnswer.text (sprintf "could not read that command: %s" reason)
        }

    /// Type into a live-only terminal (Plan 19). A refusal is an ANSWER rather than an error,
    /// like every other tool here: the model is meant to read "use execute_command there" and
    /// do that, not to see a protocol failure and try a third thing.
    let private writeTerminal (capabilities: AgentCapabilities) (id: TerminalId) (data: string) : Async<string> =
        async {
            match! capabilities.WriteTerminal id data with
            | Ok answer -> return answer
            | Error reason -> return sprintf "could not type into terminal %s: %s" (TerminalId.value id) reason
        }

    /// Read a live-only terminal (Plan 19). Same rendering as a block's output, because it is
    /// the same question — what did this print — and a model should not have to learn two
    /// shapes for one answer.
    let private readTerminal
        (capabilities: AgentCapabilities)
        (id: TerminalId)
        (from: int option)
        (waitFor: TerminalWait option)
        : Async<string> =
        async {
            match! capabilities.ReadTerminal id from waitFor with
            | Error reason -> return sprintf "could not read terminal %s: %s" (TerminalId.value id) reason
            | Ok tail when tail.Text = "" && tail.Length = 0 ->
                return sprintf "terminal %s has said nothing" (TerminalId.value id)
            | Ok tail ->
                // The bounds are stated on EVERY answer, not only when something was lost. A
                // model told the text alone cannot tell "this is everything" from "this is
                // the last 2000 characters of a day", and it will read it as the first —
                // which is how an agent concludes a boot log is three lines long.
                let where =
                    if tail.Through >= tail.Length then
                        sprintf "lines %d-%d of %d, up to date" tail.From tail.Through tail.Length
                    else
                        sprintf
                            "lines %d-%d of %d — read on with from: %d"
                            tail.From
                            tail.Through
                            tail.Length
                            tail.Through
                let omitted =
                    if tail.Elided > 0 then sprintf "\n[%d earlier characters omitted]" tail.Elided else ""
                // A timeout is an ANSWER, not an error: what was said while waiting is
                // usually where the reason it never arrived is written.
                let waited =
                    match tail.Matched, waitFor with
                    | Some false, Some wait ->
                        sprintf
                            "\n(%s did not appear within %gs — this is what it said instead)"
                            (TerminalMatch.describe wait.Until)
                            wait.TimeoutSeconds
                    | _ -> ""
                if tail.Text = "" then return sprintf "%s%s%s\n(nothing printed in these lines)" where omitted waited
                else return sprintf "%s%s%s\n%s" where omitted waited tail.Text
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

    let private removeRepo (capabilities: AgentCapabilities) (raw: string) (force: bool) : Async<string> =
        withRepo raw (fun repo ->
            async {
                match! capabilities.RemoveRepo repo force with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not remove the repo: %s" e
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
                // The tool's vocabulary is a name and some credentials; everything else a
                // sandbox can be is the file's to say, so the declaration is an empty one
                // with the forwarding filled in.
                match! capabilities.StartWorkSandbox name { SandboxDecl.empty with Forward = forward } with
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

    /// Where terminals opened from now on start (Plan 25). The sandbox defaults, so the
    /// common call is one argument.
    let private setShellProfile (capabilities: AgentCapabilities) (raw: string) (cwd: string option) : Async<string> =
        let raw = if raw = "" then SandboxRef.render SandboxRef.defaultRef else raw
        // An empty string is the CLEAR, like an absent one: a model that computed a path and
        // got nothing must not be told it set the profile to "".
        let cwd = cwd |> Option.map (fun path -> path.Trim ()) |> Option.filter (fun path -> path <> "")
        withSandbox raw (fun name ->
            async {
                match! capabilities.SetShellProfile name cwd with
                | Ok outcome -> return renderCommandOutcome outcome
                | Error e -> return sprintf "could not set the shell profile: %s" e
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
            "Run a shell command in one of this session's terminals, where the people in the session can see it and edit it while it queues, and every run is on the record. This is the only way to run anything. Pass `sandbox` to run in a named work sandbox (start_work_sandbox creates one); omit it for the default sandbox, which is where everything runs unless you say otherwise. It waits for the result and returns the exit code and output; if the terminal is busy, or the command is still going, it says so and returns a handle for check_pending instead of hanging. Read what it returns: every answer states which of those happened."
            [ ToolField.required "command" "string" "the shell command line to run, e.g. \"npm test -- --watch=false\""
              ToolField.optional "sandbox" "string" "the work sandbox to run in, e.g. \"test\"; omit for the default one"
              ToolField.optional
                  "background"
                  "boolean"
                  "true to start it and carry on without waiting — use it for long work, and for work that can run alongside other work. You will be told when it finishes." ]
            (fun args ->
                async {
                    match ToolArgs.commandSandbox args with
                    | Error e -> return Error e
                    | Ok (command, sandbox, background) ->
                        return! answered (executeCommand capabilities command sandbox background)
                })

          // The terminal verbs a person already has (Plan 20, stage 3). Deliberately the same
          // three a human uses from the list — open, close, see what there is — because the
          // agent and the people in the session are working on ONE set of terminals, and a
          // second vocabulary for the agent's would make its terminals a different kind of
          // thing on every surface that draws them.
          tool
              "open_terminal"
              "Open a terminal of your own and say what it is for. Use it to work on several things at once: each terminal runs one command at a time, so a build in one does not hold up a test in another. The name is what everyone in the session reads, so name it for the job (\"tests\", \"docs build\"). You get a terminal id back; pass it to execute_command as `terminal` to run there. There is a limit per sandbox — if you have reached it, this says so, and close_terminal is how you make room."
              [ ToolField.required "name" "string" "what this terminal is for, e.g. \"tests\""
                ToolField.optional "sandbox" "string" "the work sandbox to open it in; omit for the default one" ]
              (fun args ->
                  async {
                      match ToolArgs.nameSandbox args with
                      | Error e -> return Error e
                      | Ok (name, sandbox) ->
                          let sandbox =
                              if sandbox = "" then Ok None
                              else SandboxRef.parse sandbox |> Result.map Some
                          match sandbox with
                          | Error e -> return ToolAnswer.text (sprintf "not a sandbox: %s" e) |> Ok
                          | Ok sandbox ->
                              match! capabilities.OpenTerminal name sandbox with
                              | Ok id ->
                                  return
                                      Ok (ToolAnswer.text (sprintf "opened terminal %s for %s" (TerminalId.value id) name))
                              | Error reason -> return Ok (ToolAnswer.text reason)
                  })

          tool
              "close_terminal"
              "Close one of the terminals you opened, when you have finished with it. Its recording stays in the session for anyone to read; what ends is the shell. You can only close your own — the people here can close any of them, including yours."
              [ ToolField.required "terminal" "string" "the terminal id from open_terminal" ]
              (fun args ->
                  async {
                      match ToolArgs.string "terminal" args with
                      | Error e -> return Error e
                      | Ok raw ->
                          match TerminalId.create raw with
                          | Error e -> return Ok (ToolAnswer.text (sprintf "not a terminal id: %s" e))
                          | Ok id ->
                              match! capabilities.CloseTerminal id with
                              | Ok () -> return Ok (ToolAnswer.text (sprintf "closed terminal %s" raw))
                              | Error reason -> return Ok (ToolAnswer.text reason)
                  })

          tool
              "list_terminals"
              "See every terminal open in this session — yours and the people's — what each is for, and whether something is running in it. Use it to find out what you already have before opening another, and to pick one to run in."
              []
              (fun _ ->
                  async {
                      match! capabilities.ListTerminals () with
                      | Error reason -> return Ok (ToolAnswer.text reason)
                      | Ok [] -> return Ok (ToolAnswer.text "no terminals are open")
                      | Ok terminals ->
                          let line (t: TerminalSummary) =
                              sprintf
                                  "%s — %s%s%s%s"
                                  (TerminalId.value t.Terminal)
                                  t.Name
                                  (match t.Sandbox with
                                   | Some s when s <> SandboxRef.defaultRef -> sprintf " in %s" (SandboxRef.render s)
                                   | _ -> "")
                                  (if t.Mine then " (yours)" else "")
                                  (if t.Busy then " — running something" else "")
                          return Ok (ToolAnswer.text (terminals |> List.map line |> String.concat "\n"))
                  })

          tool
              "check_pending"
              "Pick anything back up by the handle it returned — a long build, a command queued behind a busy terminal, a program waiting on a keystroke. Works for execute_command and for any command that said it was still going. Returns the same thing the original call would have."
              [ ToolField.required "handle" "string" "the handle from execute_command, or from a command that said it was still going" ]
              (fun args ->
                  async {
                      match ToolArgs.string "handle" args with
                      | Error e -> return Error e
                      | Ok handle -> return! answered (checkPending capabilities handle)
                  })

          tool
              "write_terminal"
              "Type into a terminal you hold the keyboard for: one streaming something live — a device, a console, anything whose bytes come from outside this session — or one where a command of yours opened a full-screen program and is waiting for a keystroke. Send exactly the bytes you mean, including \"\\r\" if the thing on the other end expects a newline. On a live stream, typing takes the terminal, which everyone here can see and take back, so type what you meant to and hand it over. On a shell terminal it works only while you already hold it — otherwise use execute_command, where what you run is classified and on the record."
              [ ToolField.required "terminal" "string" "the terminal id, from the terminal that was opened for the stream"
                ToolField.required "data" "string" "the bytes to type, e.g. \"AT\\r\"" ]
              (fun args ->
                  async {
                      match ToolArgs.two "terminal" "data" args with
                      | Error e -> return Error e
                      | Ok (terminal, data) ->
                          match TerminalId.create terminal with
                          | Error e -> return Error (sprintf "not a terminal id: %s" e)
                          | Ok id -> return! ok (writeTerminal capabilities id data)
                  })

          tool
              "read_terminal"
              "Read what a terminal has said, and optionally wait for it to say something. Use it when the answer does not come back as a command's output — a terminal streaming something live, or a shell terminal where a command has opened a full-screen program and is waiting for a keystroke. With `wait_for` the read is held until that exact text appears, which is what you want after typing at a device: it answers as soon as the text arrives, and on a timeout it answers with what was said instead, which is usually where the reason it never came is written. With no `from` you get the tail: what it is saying now, capped, saying how much it left out. With `from` you get a page starting at that line and the line to carry into the next call, which is how you read what a terminal said BEFORE you arrived, however long ago. Every answer says which lines it covers and how many the terminal has, so you can tell a whole answer from the end of a long one. Reading takes nothing from anybody: whoever is typing keeps the terminal. Refused on a shell terminal running ordinary commands, where what one printed comes back from execute_command instead."
              [ ToolField.required "terminal" "string" "the terminal id, from the terminal that was opened for the stream"
                ToolField.optional
                    "from"
                    "integer"
                    "the line to read forward from, as reported by a previous read; omit for the tail"
                ToolField.optional
                    "wait_for"
                    "string"
                    "hold the read until this exact text appears, e.g. \"login: \". Literal text — for a pattern use wait_for_pattern"
                ToolField.optional
                    "wait_for_pattern"
                    "string"
                    "hold the read until output matches this pattern, e.g. \"[#$>] $\" for a shell prompt. Takes literal text, `.`, `[classes]`, `*`, `+`, `?`, `|`, groups, and `^`/`$` anchored to a LINE. No backreferences, lookaround or non-greedy quantifiers"
                ToolField.optional
                    "timeout_seconds"
                    "number"
                    "how long to hold before answering with what was said instead; default 10" ]
              (fun args ->
                  async {
                      match ToolArgs.terminalRead args with
                      | Error e -> return Error e
                      | Ok (terminal, from, waitFor) ->
                          match TerminalId.create terminal with
                          | Error e -> return Error (sprintf "not a terminal id: %s" e)
                          | Ok id -> return! ok (readTerminal capabilities id from waitFor)
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
              "Clone a GitHub repo into this session's shared repos directory (visible to everyone here, and inside the work environment). Takes owner/repo — never a URL — and only repos the session's GitHub credential can reach: GitHub says \"not found\" for a repo it will not show you, so a not-found on a repo that exists means nobody has connected GitHub here — say that rather than retrying. Answers with the checkout's path as a terminal here reaches it — usually relative to where a terminal starts, so `cd` it as given, pass it to set_shell_profile as given, and never rebuild it from a longer one. Read-only bootstrap: to commit or push, use execute_command in a terminal. Already-added repos just report their current state."
              [ ToolField.required "repo" "string" "the repo as owner/name, e.g. \"octocat/hello-world\"" ]
              (ofRepo (addRepo capabilities))

          tool
              "remove_repo"
              "Delete a repo's checkout from this session. Use it when a checkout is unreadable and add_repo told you to, and when the session is finished with a repo — everyone here sees it go from the repos list. A checkout with uncommitted changes is REFUSED unless you pass `force`, because removing it deletes that work and adding the repo again brings back the commits and nothing else: read the refusal and decide, rather than passing force by reflex. If terminals were set to start inside the checkout, they go back to starting wherever the sandbox puts them, and the answer says so. add_repo is the way back."
              [ ToolField.required "repo" "string" "the repo as owner/name, e.g. \"octocat/hello-world\""
                ToolField.optional "force" "boolean" "true to delete uncommitted changes along with the checkout" ]
              (fun args ->
                  async {
                      match ToolArgs.repoForce args with
                      | Error e -> return Error e
                      | Ok (repo, force) -> return! ok (removeRepo capabilities repo force)
                  })

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
                  })

          tool
              "set_shell_profile"
              "Say where terminals opened from now on should start. Use it once after add_repo, with the path add_repo gave you, and stop putting `cd` at the front of every command — every terminal opened afterwards starts there, yours and the people's, and it survives a restart. It takes a DIRECTORY, not a script: there is nothing to run here, and execute_command is still the only way to run anything. The directory must already exist inside that sandbox — this checks, and says so rather than leaving you a terminal that opens nowhere. A path from add_repo or the repos query goes in as given: the sandbox resolves it against the directory its terminals start in. Omit `cwd` to put it back the way it was. Terminals that are already open keep the directory they are in; the one exception is the terminal your plain execute_command runs in, which is reopened for you."
              [ ToolField.optional
                    "cwd"
                    "string"
                    "a directory the sandbox has, said the way add_repo and the repos query say it, e.g. \"repos/octocat/hello-world\"; omit it to clear the profile"
                ToolField.optional "sandbox" "string" "the work sandbox this is about; omit for the default one" ]
              (fun args ->
                  async {
                      match ToolArgs.cwdSandbox args with
                      | Error e -> return Error e
                      | Ok (cwd, sandbox) -> return! ok (setShellProfile capabilities sandbox cwd)
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
