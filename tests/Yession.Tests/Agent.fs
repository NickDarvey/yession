module Yession.Tests.Agent

// Step 08 verification: the agent turn as events.
//
// Repeatable by construction: the deterministic tests inject scripted `RunAgent`
// runners — the full lifecycle (streamed deltas -> completed | failed) is exercised
// through the orchestrator, the projection, and the real WebRTC stack (E2E-5) without
// any dependence on live model output. The real Claude Agent SDK adapter is verified by
// a smoke test that runs whenever ANTHROPIC_API_KEY is present and reports itself as
// skipped otherwise.

open System
open Fable.Pyxpecto
open Yjs
open Ylmish
open Yession.Domain
open Yession.SessionProcess
open Yession.Client
open Yession.Host
open Yession.Tests.Support

let private sessionId = SessionId.create "agent-tests" |> expect
let private turnId = AgentTurnId.create "turn-1" |> expect
let private humanMessageId = MessageId.create "msg-human" |> expect
let private agentMessageId = MessageId.create "msg-agent" |> expect
let private ada = PeerId.create "ada" |> expect

let private mintTurnId () = turnId
let private mintMessageId () = agentMessageId

let private newLog () =
    InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)

let private eventsOf (log: EventLog<SessionEvent>) =
    async {
        let! page = log.Read None Int32.MaxValue
        return page.Events |> List.map (fun e -> e.Event)
    }

let private trigger : MessageSent =
    { MessageId = humanMessageId
      DraftId = None
      QueueId = None
      Author = HumanPeer ada
      Body = "hi agent" }

let private triggerItem : ConversationItem =
    { MessageId = humanMessageId
      Author = HumanPeer ada
      Body = "hi agent"
      Status = Complete }

let private envelope (offset: int64) (event: SessionEvent) : EventEnvelope<SessionEvent> =
    { EventId = EventId.fresh ()
      SessionId = sessionId
      Offset = EventOffset.create offset |> expect
      Actor = ActorRef.Agent
      Timestamp = DateTimeOffset.UtcNow
      Event = event }

// -----------------------------------------------------------------------------
// Model tests — the orchestrator's event stream and the projection's determinism.
// -----------------------------------------------------------------------------

let private turnTests =
    testList "Agent turn" [
        testCaseAsync "a completed run appends the full lifecycle with streamed deltas" <|
            async {
                let log = newLog ()
                let scripted : RunAgent =
                    fun context _capabilities onChunk ->
                        async {
                            Expect.equal context.CurrentMessage triggerItem "the context's current message is the trigger"
                            Expect.equal context.SessionId sessionId "the context carries the session"
                            onChunk { Text = "Hel" }
                            onChunk { Text = "lo!" }
                            return AgentCompleted "Hello!"
                        }
                do! AgentTurn.run log scripted (fun _ -> AgentCapabilities.none) mintTurnId mintMessageId sessionId [ triggerItem ] trigger
                let! events = eventsOf log
                Expect.equal
                    events
                    [ AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = humanMessageId }
                      AgentContextBuilt { AgentTurnId = turnId; MessageCount = 1 }
                      AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId }
                      AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "Hel" }
                      AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "lo!" }
                      AgentMessageCompleted { AgentTurnId = turnId; MessageId = agentMessageId; Body = "Hello!" } ]
                    "the lifecycle, in order"
            }

        testCaseAsync "a failed run produces AgentTurnFailed" <|
            async {
                let log = newLog ()
                let failing : RunAgent = fun _ _ _ -> async { return AgentFailed "boom" }
                do! AgentTurn.run log failing (fun _ -> AgentCapabilities.none) mintTurnId mintMessageId sessionId [ triggerItem ] trigger
                let! events = eventsOf log
                Expect.equal
                    (List.last events)
                    (AgentTurnFailed { AgentTurnId = turnId; Reason = "boom" })
                    "the failure is an event"
            }

        testCaseAsync "a throwing run produces AgentTurnFailed, not an exception" <|
            async {
                let log = newLog ()
                let throwing : RunAgent = fun _ _ _ -> failwith "runner exploded"
                do! AgentTurn.run log throwing (fun _ -> AgentCapabilities.none) mintTurnId mintMessageId sessionId [ triggerItem ] trigger
                let! events = eventsOf log
                match List.last events with
                | AgentTurnFailed f -> Expect.equal f.Reason "runner exploded" "the thrown reason is captured"
                | other -> failwithf "expected AgentTurnFailed, got %A" other
            }

        testCase "the streamed response projects deterministically (deltas -> completed)" <| fun () ->
            let events =
                [ envelope 0L (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = humanMessageId })
                  envelope 1L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                  envelope 2L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "Hel" })
                  envelope 3L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "lo!" }) ]
            let streaming, highWater = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal
                (streaming.Items |> List.map (fun i -> i.Body, i.Status))
                [ "Hello!", Streaming ]
                "deltas accumulate into a Streaming item"

            let completed, _ =
                ConversationProjection.applyEvents
                    highWater
                    [ envelope 4L (AgentMessageCompleted { AgentTurnId = turnId; MessageId = agentMessageId; Body = "Hello!" }) ]
                    streaming
            Expect.equal
                (completed.Items |> List.map (fun i -> i.Author, i.Body, i.Status))
                [ (ActorRef.Agent, "Hello!", Complete) ]
                "completion flips the item to Complete"

            // Idempotency: re-applying the whole overlapping stream changes nothing.
            let again, _ =
                ConversationProjection.applyEvents
                    (Some (EventOffset.create 4L |> expect))
                    (events @ [ envelope 4L (AgentMessageCompleted { AgentTurnId = turnId; MessageId = agentMessageId; Body = "Hello!" }) ])
                    completed
            Expect.equal again completed "duplicate agent event pages do not double-apply"

        testCase "a turn failure marks the streaming item Failed (partial body kept)" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 0L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                      envelope 1L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "partial" })
                      envelope 2L (AgentTurnFailed { AgentTurnId = turnId; Reason = "overloaded" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Body, i.Status))
                [ "partial", ConversationItemStatus.Failed ]
                "the streaming item fails in place"

        testCase "a turn that fails before its message started still shows in the conversation" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 0L (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = humanMessageId })
                      envelope 1L (AgentTurnFailed { AgentTurnId = turnId; Reason = "context build failed" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Author, i.Body, i.Status))
                [ (ActorRef.Agent, "context build failed", ConversationItemStatus.Failed) ]
                "the failure is a Failed conversation item"
    ]

// -----------------------------------------------------------------------------
// E2E-5 — a full agent turn streams over real WebRTC into the client's timeline.
// The runner is scripted, so the flow is exercised end-to-end and stays repeatable.
// -----------------------------------------------------------------------------

let private port = 8102
let private token = "agent-e2e-token"
let private e2eSessionId = SessionId.create "agent-e2e-session" |> expect
let private signalUrl = sprintf "http://127.0.0.1:%d/signal" port

let mutable private host : Host.SessionHost option = None

let private e2eTests =
    testList "Agent E2E" [
        testCaseAsync "start the Session Process host (scripted agent)" <|
            async {
                let scripted : RunAgent =
                    fun context _capabilities onChunk ->
                        async {
                            onChunk { Text = "You said: " }
                            onChunk { Text = context.CurrentMessage.Body }
                            return AgentCompleted (sprintf "You said: %s" context.CurrentMessage.Body)
                        }
                let! h = Host.startWith (Some scripted) e2eSessionId token port
                host <- Some h
            }

        testCaseAsync "a sent message yields a streamed agent response built from events (E2E-5)" <|
            async {
                let! a = connectClient signalUrl token "ada" "Ada"
                let draftId = DraftId.create "draft-agent" |> expect
                a.Runner.Dispatch (user (StartDraftMsg draftId))
                a.Runner.Dispatch (user (editBody draftId (Text.insert 0 "hi agent") (a.Runner.Model ())))
                a.Connection.SendDraft draftId

                // The client's timeline gains the sent message and then the agent's
                // completed response — all consumed as events.
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items
                         |> List.map (fun i -> i.Author, i.Body, i.Status)) = [ (HumanPeer (peer "ada" "Ada").PeerId, "hi agent", Complete)
                                                                                (ActorRef.Agent, "You said: hi agent", Complete) ]
                        && m.Agent.ActiveTurn = None)

                // Exactly one turn per human MessageSent, with the full lifecycle.
                let h = host.Value
                let! page = h.Log.Read None Int32.MaxValue
                let kinds =
                    page.Events
                    |> List.choose (fun e ->
                        match e.Event with
                        | AgentTurnStarted _ -> Some "started"
                        | AgentContextBuilt _ -> Some "context"
                        | AgentMessageStarted _ -> Some "message"
                        | AgentMessageDelta _ -> Some "delta"
                        | AgentMessageCompleted _ -> Some "completed"
                        | AgentTurnFailed _ -> Some "failed"
                        | _ -> None)
                Expect.equal
                    kinds
                    [ "started"; "context"; "message"; "delta"; "delta"; "completed" ]
                    "one turn, streamed as events"

                // The UI renders the streamed agent message from the projection.
                let html = View.render (a.Runner.Model ())
                Expect.isTrue (html.Contains "data-message-author=\"agent\"") "the agent message renders"
                Expect.isTrue (html.Contains "You said: hi agent") "with the streamed body"

                do! a.Channel.Close ()
            }

        testCaseAsync "stop the Session Process host" <|
            async {
                match host with
                | Some h -> do! h.Stop ()
                | None -> ()
            }
    ]

// -----------------------------------------------------------------------------
// Live SDK smoke — runs the real Claude Agent SDK adapter when credentials exist.
// Gated so the suite stays deterministic without them (reported, not hidden).
// -----------------------------------------------------------------------------

let private liveTests =
    if Interop.envOr "ANTHROPIC_API_KEY" (Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "") <> "" then
        testList "Agent live SDK" [
            testCaseAsync "the real adapter completes a turn with a non-empty streamed body" <|
                async {
                    let log = newLog ()
                    let mintLiveTurn () = AgentTurnId.create (string (Guid.NewGuid ())) |> expect
                    let mintLiveMessage () = MessageId.create (string (Guid.NewGuid ())) |> expect
                    do! AgentTurn.run log Agent.run (fun _ -> AgentCapabilities.none) mintLiveTurn mintLiveMessage sessionId [ triggerItem ] trigger
                    let! events = eventsOf log
                    match List.last events with
                    | AgentMessageCompleted completed ->
                        Expect.isTrue (completed.Body.Length > 0) "the live response has a body"
                    | AgentTurnFailed f -> failwithf "live agent turn failed: %s" f.Reason
                    | other -> failwithf "expected a completed agent message, got %A" other
                }

            testCaseAsync "the live agent runs a real command through its MCP tools" <|
                async {
                    let m =
                        Manager.create
                            (Some Agent.run)
                            (Some (Backends.LocalProcessBackend.create ()))
                            8135
                    let! _ =
                        m.StartSession
                            { SessionId = SessionId.create "live-tools" |> expect
                              SessionToken = "live-tools-token" }
                    let managed = (m.Registered ()) |> List.head
                    let! a = connectClient (managed.BootstrapUri + "signal") "live-tools-token" "ada" "Ada"
                    let draftId = DraftId.create "live-tools-draft" |> expect
                    a.Runner.Dispatch (user (StartDraftMsg draftId))
                    a.Runner.Dispatch (
                        user (
                            editBody
                                draftId
                                (Text.insert 0 "Use your execute_command tool to run the executable `node` with arguments `-e` and `console.log(6*7)`, then reply with just the number it printed.")
                                (a.Runner.Model ())))
                    a.Connection.SendDraft draftId

                    do! a.Runner.WaitFor (fun model ->
                            model.Conversation.Items
                            |> List.exists (fun i -> i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "42"))

                    // The command ran through the scoped capability: its lifecycle is
                    // in the event log and the environment started lazily for it.
                    let! page = managed.Host.Log.Read None Int32.MaxValue
                    let sawCommand =
                        page.Events
                        |> List.exists (fun e ->
                            match e.Event with
                            | CommandCompleted c -> c.Result = CommandSucceeded 0
                            | _ -> false)
                    let sawEnvironment =
                        page.Events
                        |> List.exists (fun e -> match e.Event with EnvironmentStarted _ -> true | _ -> false)
                    Expect.isTrue sawCommand "the command lifecycle is events"
                    Expect.isTrue sawEnvironment "the environment started lazily for the tool call"

                    do! a.Channel.Close ()
                    do! m.Stop ()
                }
        ]
    else
        testList "Agent live SDK" [
            testCase "skipped: no agent credentials (ANTHROPIC_API_KEY / CLAUDE_CODE_OAUTH_TOKEN) in this environment" <| fun () ->
                Expect.isTrue true "gated live test skipped"
        ]

let tests =
    testList "Agent" [
        turnTests
        e2eTests
        liveTests
    ]
