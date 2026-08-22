module Yession.Tests.Tools

// The tool registry (Plan 16, part A). What is worth pinning is not "a list holds
// descriptors" — it is the four properties everything downstream leans on:
//
//   * the auto-approve list is COMPUTED from the descriptors, so the model's tool list and
//     what runs without a permission round-trip cannot drift apart;
//   * a namespace is what lets two providers both offer `list`, and the call still reaches
//     the right one;
//   * a genuine collision is REFUSED rather than resolved, because silently shadowing one
//     tool with another shows up only as the wrong thing happening;
//   * a tool that ran and failed is not the same as a call that never happened — the model
//     must be able to read the first and act on it.
//
// And one property that is about the NEXT part rather than this one: `set_secret`'s value
// declares its own secrecy, in the schema, where the redactor will look for it.

open Fable.Pyxpecto
open Yession.Domain

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private call ns name args : ToolCall = { Namespace = ns; Name = name; Arguments = args }

/// A registry of one tool that answers with its own namespace, so a dispatch test can see
/// WHERE a call landed rather than merely that it landed.
let private echo (ns: string) (name: string) : ToolRegistry =
    ToolRegistry.ofNamespace
        ns
        [ ToolDescriptor.create ns name (sprintf "the %s tool of %s" name ns) ToolSchema.none ]
        (fun c -> async { return Ok (ToolAnswer.text (sprintf "%s/%s" c.Namespace c.Name)) })

let private registryTests =
    testList "the registry" [

        test "the auto-approve list is computed from the descriptors, not kept beside them" {
            let registry = echo "serial" "list_devices"
            Expect.equal
                (ToolRegistry.allowedTools registry)
                [ "mcp__serial__list_devices" ]
                "the wire name is mcp__<namespace>__<tool>"
        }

        // The namespace is not decoration: it is the whole reason a session can be handed a
        // provider that happens to have chosen a name we already use.
        test "two namespaces may offer the same tool name, and a call reaches its own" {
            let registry = ToolRegistry.merge (echo "yession" "list") (echo "serial" "list") |> expect
            Expect.equal
                (ToolRegistry.allowedTools registry)
                [ "mcp__yession__list"; "mcp__serial__list" ]
                "both survive the merge, under distinct wire names"
            Expect.equal (ToolRegistry.namespaces registry) [ "yession"; "serial" ] "one server each"
        }

        testCaseAsync "…and the call is dispatched to the namespace it named" <|
            async {
                let registry = ToolRegistry.merge (echo "yession" "list") (echo "serial" "list") |> expect
                let! first = registry.Invoke (call "yession" "list" "{}")
                let! second = registry.Invoke (call "serial" "list" "{}")
                Expect.equal first (Ok (ToolAnswer.text "yession/list")) "the first registry answered its own"
                Expect.equal second (Ok (ToolAnswer.text "serial/list")) "and the second answered its own"
            }

        // Resolving a collision by letting one win would make the declared list and the
        // callable set differ, and the difference is invisible until the wrong tool runs.
        test "a genuine collision is refused, and the refusal names the doubled tool" {
            match ToolRegistry.merge (echo "serial" "list") (echo "serial" "list") with
            | Ok _ -> failwith "expected the merge to be refused"
            | Error reason ->
                Expect.stringContains reason "mcp__serial__list" "the refusal says which name is doubled"
        }

        testCaseAsync "a call to a tool nobody declared is an error, not a silent nothing" <|
            async {
                let! answer = (echo "serial" "list").Invoke (call "serial" "acquire" "{}")
                match answer with
                | Ok answer -> failwithf "expected a refusal, got %s" answer.Text
                | Error reason -> Expect.stringContains reason "serial/acquire" "it names what was asked for"
            }

        testCaseAsync "a registry does not answer for a namespace it does not own" <|
            async {
                let! answer = (echo "serial" "list").Invoke (call "yession" "list" "{}")
                Expect.isError answer "a namespace it never declared is not its call to take"
            }
    ]

// --- the yession namespace ------------------------------------------------------------

let private capabilities (execute: ExecuteCommand) : AgentCapabilities =
    { AgentCapabilities.none with ExecuteCommand = execute }

let private ran (command: string) : TerminalCommandOutcome =
    { Terminal = TerminalId.create "t1" |> expect
      Handle = QueueId.create "q1" |> expect
      Block = None
      Status = TerminalCommandRan (CommandSucceeded 0)
      OutputTail = command
      Elided = 0 }

let private queryDef (raw: string) : QueryDef =
    { Name = QueryName.create raw |> expect
      Title = raw
      Description = sprintf "the %s of this session" raw
      Shape = Value }

/// Every property of a tool's schema, paired with whether it declares itself write-only.
let private writeOnly (schema: string) : (string * bool) list =
    let property =
        Decode.object (fun get -> get.Optional.Field "writeOnly" Decode.bool |> Option.defaultValue false)
    Decode.fromString (Decode.field "properties" (Decode.keyValuePairs property)) schema |> expect

/// `read_terminal` against a capability that answers with exactly this tail.
let private readingTerminal (tail: TerminalTail) =
    let registry =
        AgentTools.registry { AgentCapabilities.none with ReadTerminal = fun _ _ _ -> async { return Ok tail } }
    registry.Invoke (call "yession" "read_terminal" """{"terminal":"t1"}""")

let private sessionTests =
    testList "the yession namespace" [

        test "every verb is declared once, under the names the model already knew" {
            let registry = AgentTools.registry AgentCapabilities.none
            let allowed = ToolRegistry.allowedTools registry
            let expected =
                [ "execute_command"; "check_pending"; "set_secret"; "list_secrets"
                  "delete_secret"; "add_repo"; "switch_branch"; "fetch_repo"
                  "repo_status"; "repo_log"; "repo_diff"
                  "start_work_sandbox"; "stop_work_sandbox"; "set_shell_profile" ]
                |> List.map (sprintf "mcp__yession__%s")
            Expect.containsAll allowed expected "every verb the agent had before is still declared"
            Expect.equal (List.length allowed) (List.length (List.distinct allowed)) "no name is declared twice"
        }

        // Declaring a query is what puts it in front of the agent — there is no second list
        // of query tools to keep in step, which is the whole point of Plan 15's registry.
        test "a registered query becomes a read-only tool, without anyone writing one" {
            let registry =
                AgentTools.registry { AgentCapabilities.none with Queries = [ queryDef "repos" ] }
            let repos = registry.Tools |> List.find (fun t -> t.Name = "repos")
            Expect.isTrue repos.ReadOnly "a query carries MCP's own readOnlyHint"
            Expect.equal repos.Title (Some "repos") "and the title the settings surface shows"
            Expect.equal (writeOnly repos.InputSchema) [] "a query is nullary"
        }

        // The shell profile (Plan 25). What matters at this seam is that the tool's
        // arguments reach the capability as the capability's own vocabulary — a directory
        // or its absence — because "no directory" is the CLEAR and not a missing argument.
        testCaseAsync "set_shell_profile hands the capability a directory" <|
            async {
                let mutable seen : (string * string option) option = None
                let registry =
                    AgentTools.registry
                        { AgentCapabilities.none with
                            SetShellProfile =
                              fun name cwd ->
                                async {
                                    seen <- Some (SandboxName.value name, cwd)
                                    return Ok { Status = CommandRan "set"; Tool = "set_shell_profile"; Summary = "s"; Handle = None }
                                } }
                let! _ =
                    registry.Invoke (call "yession" "set_shell_profile" """{"cwd":"/repos/octo/hello"}""")
                Expect.equal seen (Some ("default", Some "/repos/octo/hello")) "the default sandbox, and the path"
            }

        testCaseAsync "a set_shell_profile with no directory is the clear" <|
            async {
                let mutable seen : (string * string option) option = None
                let registry =
                    AgentTools.registry
                        { AgentCapabilities.none with
                            SetShellProfile =
                              fun name cwd ->
                                async {
                                    seen <- Some (SandboxName.value name, cwd)
                                    return Ok { Status = CommandRan "cleared"; Tool = "set_shell_profile"; Summary = "s"; Handle = None }
                                } }
                let! _ = registry.Invoke (call "yession" "set_shell_profile" """{"sandbox":"test"}""")
                Expect.equal seen (Some ("test", None)) "an absent directory is the clear, not a missing argument"
            }

        testCaseAsync "a refused shell profile reads as a refusal, not a failure" <|
            async {
                let registry =
                    AgentTools.registry
                        { AgentCapabilities.none with
                            SetShellProfile =
                              fun _ _ ->
                                async {
                                    return
                                        Ok
                                            { Status = CommandRefusedBy (PeerRef (PeerId.create "ada" |> expect), Some "not there")
                                              Tool = "set_shell_profile"
                                              Summary = "set_shell_profile default -> /gone"
                                              Handle = None }
                                } }
                let! answer = registry.Invoke (call "yession" "set_shell_profile" """{"cwd":"/gone"}""")
                match answer with
                | Ok answer -> Expect.isTrue (answer.Text.StartsWith "REFUSED by") "the model reads a decision, not a malfunction"
                | Error e -> failwithf "expected an answer, got %s" e
            }

        // The mechanism the tool-use record will rely on: secrecy is a property of the
        // FIELD, declared in the schema, so renaming an argument cannot leave the
        // declaration pointing at nothing.
        test "set_secret's value declares itself write-only, and its name does not" {
            let setSecret =
                (AgentTools.registry AgentCapabilities.none).Tools
                |> List.find (fun t -> t.Name = "set_secret")
            Expect.equal
                (writeOnly setSecret.InputSchema |> List.sortBy fst)
                [ "name", false; "value", true ]
                "only the value is a secret"
        }

        testCaseAsync "a call reaches the capability behind it, with its arguments" <|
            async {
                let mutable seen = ""
                let registry =
                    AgentTools.registry (capabilities (fun (request: CommandRequest) ->
                        async {
                            seen <- request.Command
                            return Ok (ran request.Command)
                        }))
                let! answer = registry.Invoke (call "yession" "execute_command" """{"command":"echo hi"}""")
                Expect.equal seen "echo hi" "the argument was decoded and passed through"
                Expect.equal answer (Ok (ToolAnswer.text "exit code 0 in terminal t1\necho hi")) "and the outcome came back as text"
            }

        // The distinction the SDK is told about as `isError`: arguments that cannot be read
        // mean the call never happened, and that is not the same as a command that failed.
        testCaseAsync "arguments that cannot be read are a refusal, not a failed command" <|
            async {
                let registry = AgentTools.registry (capabilities (fun _ -> async { return Ok (ran "") }))
                let! answer = registry.Invoke (call "yession" "execute_command" "{}")
                Expect.isError answer "no command argument, so no call"
            }

        testCaseAsync "a capability that says no answers as text the model can act on" <|
            async {
                let registry =
                    AgentTools.registry (capabilities (fun _ -> async { return Error "no terminal capability" }))
                let! answer = registry.Invoke (call "yession" "execute_command" """{"command":"ls"}""")
                Expect.equal
                    answer
                    (Ok (ToolAnswer.text "could not run the command: no terminal capability"))
                    "the call happened; it is the command that did not"
            }

        // Reading a terminal with no blocks (Plan 19), paged in Plan 25. Every answer says
        // where it read, because a model told only the text cannot tell "this is everything"
        // from "this is the last 2000 characters of a day" — and it reads it as the first.
        testCaseAsync "a read that reached the live edge says it is up to date" <|
            async {
                let! answer =
                    readingTerminal { Text = "U-Boot 2024.01\n=> "; Elided = 0; From = 0; Through = 2; Length = 2; Matched = None }
                Expect.equal
                    answer
                    (Ok (ToolAnswer.text "lines 0-2 of 2, up to date\nU-Boot 2024.01\n=> "))
                    "the text, and the fact that there is no more of it"
            }

        // The cursor is the whole point of a page: a reader that cannot say where it stopped
        // cannot read on, and a model that is not handed the number will not invent it.
        testCaseAsync "a read with more behind it hands back the line to carry on from" <|
            async {
                let! answer =
                    readingTerminal { Text = "boot\n"; Elided = 0; From = 0; Through = 500; Length = 1200; Matched = None }
                Expect.equal
                    answer
                    (Ok (ToolAnswer.text "lines 0-500 of 1200 — read on with from: 500\nboot\n"))
                    "where it got to, and how to continue"
            }

        // What matters is the SAME thing that matters for a block's output: a model that
        // cannot tell a short answer from a truncated one describes the wrong thing
        // confidently.
        testCaseAsync "a tail that left something out says how much" <|
            async {
                let! answer = readingTerminal { Text = "=> "; Elided = 4096; From = 700; Through = 1200; Length = 1200; Matched = None }
                Expect.equal
                    answer
                    (Ok (ToolAnswer.text "lines 700-1200 of 1200, up to date\n[4096 earlier characters omitted]\n=> "))
                    "in the words a block's output already uses"
            }

        // Two fields rather than a mode flag, and this is what it buys: a caller that means
        // a pattern and sets the literal field gets a refusal instead of a literal match on a
        // regex string — which would be wrong, silent, and look exactly like a device that
        // never answered.
        testCaseAsync "asking for a literal and a pattern at once is refused" <|
            async {
                let registry = AgentTools.registry AgentCapabilities.none
                let! answer =
                    registry.Invoke (
                        call "yession" "read_terminal"
                            ("""{"terminal":"t1","wait_for":"x","wait_for_pattern":"y"}"""))
                match answer with
                | Error reason -> Expect.stringContains reason "give one" "and says what to do about it"
                | Ok other -> failwithf "expected a refusal, got %A" other
            }

        // Compiled at the boundary, so a pattern outside the subset is an answer to THIS call
        // rather than something discovered part-way through a wait that then has to explain
        // why it stopped.
        testCaseAsync "a pattern outside the subset is refused at the call, and named" <|
            async {
                let registry = AgentTools.registry AgentCapabilities.none
                let! answer =
                    registry.Invoke (
                        call "yession" "read_terminal"
                            ("""{"terminal":"t1","wait_for_pattern":"(a)\\1"}"""))
                match answer with
                | Error reason -> Expect.stringContains reason "backreference" "the construct, by name"
                | Ok other -> failwithf "expected a refusal, got %A" other
            }

        testCaseAsync "a terminal that has said nothing says so, rather than answering blank" <|
            async {
                let! answer = readingTerminal { Text = ""; Elided = 0; From = 0; Through = 0; Length = 0; Matched = None }
                Expect.equal answer (Ok (ToolAnswer.text "terminal t1 has said nothing")) "silence is an answer"
            }

        // The refusal is the interesting half: it has to send the model somewhere that
        // works, not merely say no.
        testCaseAsync "reading an instrumented terminal is refused, and names what to use" <|
            async {
                let registry =
                    AgentTools.registry
                        { AgentCapabilities.none with
                            ReadTerminal =
                                fun _ _ _ ->
                                    async {
                                        return Error "this terminal runs commands as blocks — what one printed comes back from execute_command"
                                    } }
                let! answer = registry.Invoke (call "yession" "read_terminal" """{"terminal":"t1"}""")
                match answer with
                | Ok text -> Expect.stringContains text.Text "execute_command" "it says where to go instead"
                | Error e -> failwithf "a refusal is an answer, not a failed call: %s" e
            }
    ]

// --- the audit seam (Plan 16, part C) ---------------------------------------------------

/// A log that keeps what it was told, so a test can read it back.
let private recorder () =
    let started = ResizeArray<ToolUseBegin> ()
    let finished = ResizeArray<ToolUseEnd> ()
    let log : ToolUseLog =
        { Started =
            fun begun ->
                async {
                    started.Add begun
                    return Some (ToolUseId.create (sprintf "use-%d" started.Count) |> expect)
                }
          Finished =
            fun _ ended ->
                async { finished.Add ended } }
    log, started, finished

let private auditTests =
    testList "the audit seam" [

        // THE test of this part. `set_secret` was recorded nowhere at all before it, and the
        // log is served in immutable cacheable chunks — so a value that reached it would be
        // durable, fetchable and unrecallable.
        testCaseAsync "set_secret records its name and never its value" <|
            async {
                let log, started, _ = recorder ()
                let registry =
                    AgentTools.registry AgentCapabilities.none |> ToolUseLog.wrap log
                let! _ =
                    registry.Invoke
                        (call "yession" "set_secret" """{"name":"DEPLOY_TOKEN","value":"hunter2"}""")
                let recorded = (Seq.head started).Arguments |> Option.defaultValue ""
                Expect.stringContains recorded "DEPLOY_TOKEN" "the name is the whole point of the record"
                Expect.isFalse (recorded.Contains "hunter2") "the value never reached the record"
            }

        // Schema-driven, so no tool carries logging code and a tool added later is covered
        // by having declared its fields rather than by remembering this exists.
        test "the redactor drops every writeOnly field, for any schema" {
            let schema =
                ToolSchema.ofFields
                    [ ToolField.required "keep" "string" "recorded"
                      ToolField.secret "drop" "never recorded"
                      ToolField.secret "also" "never recorded" ]
            let recorded =
                ToolArguments.redact schema """{"keep":"yes","drop":"a","also":"b"}"""
                |> Option.defaultValue ""
            Expect.stringContains recorded "yes" "an unmarked field is recorded"
            Expect.isFalse (recorded.Contains "\"a\"") "a marked field is not"
            Expect.isFalse (recorded.Contains "\"b\"") "nor the second one"
        }

        // We do not control a foreign schema, so no marking in it can be trusted. The record
        // still says WHERE the call went and how it ended, which is the part that answers
        // "what did the agent just do".
        testCaseAsync "a foreign tool's call records no argument values at all" <|
            async {
                let log, started, _ = recorder ()
                let foreign =
                    ToolRegistry.ofNamespace
                        "serial"
                        [ ToolDescriptor.foreign "serial" "acquire_device" "claim a device"
                            (ToolSchema.ofFields [ ToolField.required "serial_number" "string" "which one" ]) ]
                        (fun _ -> async { return Ok (ToolAnswer.text "claimed") })
                let! _ =
                    (ToolUseLog.wrap log foreign).Invoke
                        (call "serial" "acquire_device" """{"serial_number":"A700eXYZ"}""")
                let begun = Seq.head started
                Expect.equal begun.Namespace "serial" "the namespace is recorded"
                Expect.equal begun.Name "acquire_device" "and the tool"
                Expect.equal begun.Arguments None "and nothing of the arguments"
            }

        testCaseAsync "a call that became a block says so, so the chat does not draw it twice" <|
            async {
                let log, _, finished = recorder ()
                let block = BlockId.create "blk-1" |> expect
                let registry =
                    AgentTools.registry
                        (capabilities (fun (request: CommandRequest) -> async { return Ok { ran request.Command with Block = Some block } }))
                    |> ToolUseLog.wrap log
                let! _ = registry.Invoke (call "yession" "execute_command" """{"command":"ls"}""")
                Expect.equal (Seq.head finished) { Outcome = ToolCallOk; Block = Some block } "the block travelled to the record"
            }

        // An audit that loses exactly the calls that went worst is worse than none.
        testCaseAsync "a tool body that throws is still recorded, as a failure" <|
            async {
                let log, started, finished = recorder ()
                let exploding =
                    ToolRegistry.ofNamespace
                        "yession"
                        [ ToolDescriptor.create "yession" "boom" "throws" ToolSchema.none ]
                        (fun _ -> async { return failwith "kaboom" })
                let! answer = (ToolUseLog.wrap log exploding).Invoke (call "yession" "boom" "{}")
                Expect.isError answer "the caller is told, as a value"
                Expect.equal (Seq.length started) 1 "the call was recorded as it started"
                match (Seq.head finished).Outcome with
                | ToolCallFailed reason -> Expect.stringContains reason "kaboom" "and the reason survived"
                | ToolCallOk -> failwith "expected a failure"
            }
    ]

let tests = testList "Tools" [ registryTests; sessionTests; auditTests ]
