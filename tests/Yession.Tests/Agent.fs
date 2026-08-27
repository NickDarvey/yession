module Yession.Tests.Agent

// Step 08 verification: the agent turn as events.
//
// Repeatable by construction: the deterministic tests inject scripted `RunAgent`
// runners — the full lifecycle (streamed deltas -> completed | failed) is exercised
// through the orchestrator, the projection, and the real WebRTC stack (E2E-5) without
// any dependence on live model output. The real Claude Agent SDK adapter is verified by
// a smoke test carrying the `LiveAgent` capability — it runs in any tier that asks for
// one, and stands in a visible skip in the tiers that do not.

open System
open Fable.Core
open Fable.Pyxpecto
open Yjs
open Ylmish
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Chat
open Yession.SessionProcess
open Yession.App
open Yession.Host
open Yession.Tests.Support
open Yession.Domain.Prs

[<ImportAll("node:fs")>]
let private nodeFs : obj = Fable.Core.Util.jsNative

[<Emit("$0.mkdirSync($1, { recursive: true })")>]
let private mkdirSync (fs: obj) (path: string) : unit = Fable.Core.Util.jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFileSync (fs: obj) (path: string) (text: string) : unit = Fable.Core.Util.jsNative

[<ImportAll("node:path")>]
let private nodePath : obj = Fable.Core.Util.jsNative

[<Emit("$0.resolve($1)")>]
let private resolvePath (path: obj) (relative: string) : string = Fable.Core.Util.jsNative

let private sessionId = SessionId.create "agent-tests" |> expect
let private turnId = AgentTurnId.create "turn-1" |> expect
let private humanMessageId = MessageId.create "msg-human" |> expect
let private agentMessageId = MessageId.create "msg-agent" |> expect
let private ada = PeerId.create "ada" |> expect
let private bob = PeerId.create "bob" |> expect

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
      QueueId = None
      Author = PeerRef ada
      Body = "hi agent" }

let private triggerItem : ConversationItem =
    { MessageId = humanMessageId
      Author = PeerRef ada
      Body = "hi agent"
      Status = Complete
      Kind = ConversationItemKind.Message
      Offset = EventOffset.zero
      Woke = None }

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
                    fun context _capabilities _signal onChunk ->
                        async {
                            Expect.equal context.CurrentMessage (Some triggerItem) "the context's current message is the trigger"
                            Expect.equal context.SessionId sessionId "the context carries the session"
                            onChunk { Text = "Hel" }
                            onChunk { Text = "lo!" }
                            return AgentCompleted ("Hello!", None)
                        }
                do! AgentTurn.run log scripted AgentAbortSignal.none (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintTurnId mintMessageId sessionId [ triggerItem ] [] None (AgentTurn.FromMessage trigger)
                let! events = eventsOf log
                Expect.equal
                    events
                    [ AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = Some (humanMessageId); Woke = None }
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
                let failing : RunAgent = fun _ _ _ _ -> async { return AgentFailed ("boom", None) }
                do! AgentTurn.run log failing AgentAbortSignal.none (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintTurnId mintMessageId sessionId [ triggerItem ] [] None (AgentTurn.FromMessage trigger)
                let! events = eventsOf log
                Expect.equal
                    (List.last events)
                    (AgentTurnFailed { AgentTurnId = turnId; Reason = "boom" })
                    "the failure is an event"
            }

        testCaseAsync "a throwing run produces AgentTurnFailed, not an exception" <|
            async {
                let log = newLog ()
                let throwing : RunAgent = fun _ _ _ _ -> failwith "runner exploded"
                do! AgentTurn.run log throwing AgentAbortSignal.none (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintTurnId mintMessageId sessionId [ triggerItem ] [] None (AgentTurn.FromMessage trigger)
                let! events = eventsOf log
                match List.last events with
                | AgentTurnFailed f -> Expect.equal f.Reason "runner exploded" "the thrown reason is captured"
                | other -> failwithf "expected AgentTurnFailed, got %A" other
            }

        testCase "the streamed response projects deterministically (deltas -> completed)" <| fun () ->
            let events =
                [ envelope 0L (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = Some (humanMessageId); Woke = None })
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

        testCase "an interrupt marks the streaming item Interrupted (partial body kept); late deltas no longer apply" <| fun () ->
            let interruptedBy = PeerId.create "ada" |> expect
            let projection, highWater =
                ConversationProjection.applyEvents
                    None
                    [ envelope 0L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                      envelope 1L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "partial" })
                      envelope 2L (AgentTurnInterrupted { AgentTurnId = turnId; RequestedBy = interruptedBy }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Body, i.Status))
                [ "partial", ConversationItemStatus.Interrupted ]
                "the streaming item is interrupted in place, partial body kept"
            // A delta that raced past the interrupt cannot mutate the terminal item.
            let after, _ =
                ConversationProjection.applyEvents
                    highWater
                    [ envelope 3L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = " too late" }) ]
                    projection
            Expect.equal
                (after.Items |> List.map (fun i -> i.Body, i.Status))
                [ "partial", ConversationItemStatus.Interrupted ]
                "late deltas are ignored once the item left Streaming"

        testCase "a turn failure marks the streaming item Failed, keeping what it said and adding why it stopped" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 0L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                      envelope 1L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "partial" })
                      envelope 2L (AgentTurnFailed { AgentTurnId = turnId; Reason = "overloaded" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Body, i.Status))
                [ "partial\n\noverloaded", ConversationItemStatus.Failed ]
                "the streaming item fails in place"

        // The screenshot case, and the one the projection used to drop on the floor: a turn
        // that spent itself on tool calls and never streamed a word. The reason was in the
        // event log and nowhere a reader — or the NEXT turn, which reads this projection as
        // its transcript — could reach it, so an empty red item was the whole account.
        testCase "a turn that said nothing before it failed still says why" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 0L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                      envelope 1L (AgentTurnFailed { AgentTurnId = turnId; Reason = "agent run ended: error_during_execution" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Body, i.Status))
                [ "agent run ended: error_during_execution", ConversationItemStatus.Failed ]
                "the reason is the item's account of itself"

        // Placement, which the body alone cannot pin. An agent message is created when the
        // turn STARTS — before the model has spoken and before a single tool call — so a
        // tool-only turn holds an empty item above every command it goes on to run. Joining
        // the reason to that placeholder filed the account of a failure at the top of the
        // work it ended: in one real session, a hundred and forty rows above it, directly
        // under the message that had asked for it.
        testCase "the reason a silent turn stopped is anchored where it stopped" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 1L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                      envelope 9L (AgentTurnFailed { AgentTurnId = turnId; Reason = "Reached maximum number of turns (32)" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> EventOffset.value i.Offset))
                [ 9L ]
                "the failure sits at the offset it happened at, not at the turn's first event"

        // The other half of the same rule: a turn that DID speak keeps the place it spoke
        // in. Its words were said there, and moving them to where the turn later died would
        // reorder the conversation around a fact about the ending.
        testCase "a turn that spoke keeps the place it spoke in" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 1L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })
                      envelope 2L (AgentMessageDelta { AgentTurnId = turnId; MessageId = agentMessageId; Delta = "on it" })
                      envelope 9L (AgentTurnFailed { AgentTurnId = turnId; Reason = "overloaded" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> EventOffset.value i.Offset, i.Body))
                [ 1L, "on it\n\noverloaded" ]
                "the item stays where it was said, wearing the reason it stopped"

        testCase "a turn that fails before its message started still shows in the conversation" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ envelope 0L (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = Some (humanMessageId); Woke = None })
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
let private e2eSessionId = SessionId.create "agent-e2e-session" |> expect
let private signalUrl = sprintf "http://127.0.0.1:%d/signal" port

let mutable private host : Host.SessionHost option = None

let private e2eTests =
    testList "Agent E2E" [
        testCaseAsync "start the Session Process host (scripted agent)" <|
            async {
                let scripted : RunAgent =
                    fun context _capabilities _signal onChunk ->
                        async {
                            onChunk { Text = "You said: " }
                            onChunk { Text = (context.CurrentMessage |> Option.map (fun m -> m.Body) |> Option.defaultValue "") }
                            return AgentCompleted (sprintf "You said: %s" (context.CurrentMessage |> Option.map (fun m -> m.Body) |> Option.defaultValue ""), None)
                        }
                let! h = Host.startWith (Some scripted) e2eSessionId port
                host <- Some h
            }

        testCaseAsync "a sent message yields a streamed agent response built from events (E2E-5)" <|
            async {
                let! a = connectClient signalUrl (host.Value.MintPeerToken ()) "ada" "Ada"
                do! compose a a.Hello.PeerId "hi agent"
                a.Connection.SendDraft a.Hello.PeerId

                // The client's timeline gains the sent message and then the agent's
                // completed response — all consumed as events.
                do! a.Runner.WaitFor (fun m ->
                        (m.Conversation.Items
                         |> List.map (fun i -> i.Author, i.Body, i.Status)) = [ (PeerRef (peer "ada" "Ada").PeerId, "hi agent", Complete)
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
                let html = Support.render (a.Runner.Model ())
                Expect.isTrue (html.Contains (Dom.attr Dom.Hooks.messageAuthor Dom.Text.agent)) "the agent message renders"
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
// Live SDK smoke — drives the real Claude Agent SDK adapter. Gated by the
// `LiveAgent` capability alone (see the `Tag.needs` at the bottom of this file):
// a tier that asks for it has credentials, because `check` refuses to start
// otherwise. There is deliberately no second credential check here — a suite that
// re-gates itself turns a missing credential back into a silent skip.
// -----------------------------------------------------------------------------

let private liveTests =
    testList "Agent live SDK" [
        testCaseAsync "the real adapter completes a turn with a non-empty streamed body" <|
            async {
                let log = newLog ()
                let mintLiveTurn () = AgentTurnId.create (string (Guid.NewGuid ())) |> expect
                let mintLiveMessage () = MessageId.create (string (Guid.NewGuid ())) |> expect
                do! AgentTurn.run log (Agent.run Launch.unlaunched.DataDir) AgentAbortSignal.none (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintLiveTurn mintLiveMessage sessionId [ triggerItem ] [] None (AgentTurn.FromMessage trigger)
                let! events = eventsOf log
                match List.last events with
                | AgentMessageCompleted completed ->
                    Expect.isTrue (completed.Body.Length > 0) "the live response has a body"
                | AgentTurnFailed f -> failwithf "live agent turn failed: %s" f.Reason
                | other -> failwithf "expected a completed agent message, got %A" other
            }

        testCaseAsync "the SDK accepts a RESOLVED credential through the env override (Plan 08 dispatch path)" <|
            async {
                // Run the ambient credential through `Agent.runWith (Some ...)` — the
                // exact shape a broker-resolved token takes — proving the spawned
                // CLI honors options.env with the ambient variables displaced.
                let credential =
                    match Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "" with
                    | "" -> "ANTHROPIC_API_KEY", Interop.envOr "ANTHROPIC_API_KEY" ""
                    | token -> "CLAUDE_CODE_OAUTH_TOKEN", token
                let log = newLog ()
                let mintLiveTurn () = AgentTurnId.create (string (Guid.NewGuid ())) |> expect
                let mintLiveMessage () = MessageId.create (string (Guid.NewGuid ())) |> expect
                do! AgentTurn.run log (Agent.runWith Launch.unlaunched.DataDir (Some credential)) AgentAbortSignal.none (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintLiveTurn mintLiveMessage sessionId [ triggerItem ] [] None (AgentTurn.FromMessage trigger)
                let! events = eventsOf log
                match List.last events with
                | AgentMessageCompleted completed ->
                    Expect.isTrue (completed.Body.Length > 0) "the resolved-credential turn has a body"
                | AgentTurnFailed f -> failwithf "resolved-credential turn failed: %s" f.Reason
                | other -> failwithf "expected a completed agent message, got %A" other
            }

        testCaseAsync "the live agent runs a real command through its MCP tools" <|
            async {
                let m =
                    Manager.create
                        (Some (Agent.run Launch.unlaunched.DataDir))
                        (Some (fun sid -> Sandboxes.forBackend HostBackend (SessionId.value sid) EnvironmentSpec.defaults |> expect))
                        8135
                let! _ =
                    m.StartSession
                        { SessionLaunchRequest.SessionId = SessionId.create "live-tools" |> expect }
                let managed = (m.Registered ()) |> List.head
                let! a = connectClient (managed.BootstrapUri + "signal") (managed.Host.MintPeerToken ()) "ada" "Ada"
                // A shell command LINE, not an executable plus argv — that is what
                // `execute_command` takes after Plan 13 stage 3b.
                do! compose a a.Hello.PeerId "Use your execute_command tool to run `node -e 'console.log(6*7)'`, then reply with just the number it printed."
                a.Connection.SendDraft a.Hello.PeerId

                do! a.Runner.WaitFor (fun model ->
                        model.Conversation.Items
                        |> List.exists (fun i -> i.Author = ActorRef.Agent && i.Status = Complete && i.Body.Contains "42"))

                // The command ran through the scoped capability, and its lifecycle is a
                // TERMINAL BLOCK in the event log (Plan 13, stage 3b): the Step-13 command
                // events retired with the merged tool. The environment still started lazily
                // for it — opening the agent's terminal is what identifies the need now.
                let! page = managed.Host.Log.Read None Int32.MaxValue
                let sawCommand =
                    page.Events
                    |> List.exists (fun e ->
                        match e.Event with
                        | SessionEvent.TerminalBlockCompleted b -> b.Result = CommandSucceeded 0
                        | _ -> false)
                let sawEnvironment =
                    page.Events
                    |> List.exists (fun e -> match e.Event with EnvironmentStarted _ -> true | _ -> false)
                Expect.isTrue sawCommand "the command ran as a block, and its lifecycle is events"
                Expect.isTrue sawEnvironment "the environment started lazily for the tool call"

                do! a.Channel.Close ()
                do! m.Stop ()
            }

        testCaseAsync "the built-in tools are gone: the live agent cannot read a host file" <|
            async {
                // The turn's tool surface is exactly the `yession` MCP tools
                // (`tools: []` in the adapter drops every built-in), and these
                // capabilities are `none`, so `execute_command` cannot run either.
                // A nonce no model can guess is therefore unreachable — a body that
                // contains it means a built-in file/shell tool came back.
                //
                // The nonce lives ONLY in the file's CONTENTS, never in its name: a
                // nonce in the path is in the prompt, and a correct refusal that
                // quotes the path back ("I have no tool that can read
                // /…/<nonce>.txt") then reads as a leak. That false positive is what
                // failed the first release this suite ever actually ran in.
                let nonce = sprintf "yession-nonce-%s" (string (Guid.NewGuid ()))
                let dir = "tests/Yession.Tests/out/.data"
                // Absolute, so the probe would succeed if a built-in file tool were
                // back — whatever cwd the spawned CLI runs in.
                let path = resolvePath nodePath (sprintf "%s/tool-surface-probe-%s.txt" dir (string (Guid.NewGuid ())))
                mkdirSync nodeFs dir
                writeFileSync nodeFs path nonce
                let body = sprintf "Read the file at %s and reply with its exact contents." path
                let probe = { trigger with Body = body }
                let probeItem = { triggerItem with Body = body }
                let log = newLog ()
                let mintLiveTurn () = AgentTurnId.create (string (Guid.NewGuid ())) |> expect
                let mintLiveMessage () = MessageId.create (string (Guid.NewGuid ())) |> expect
                do! AgentTurn.run log (Agent.run Launch.unlaunched.DataDir) AgentAbortSignal.none (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintLiveTurn mintLiveMessage sessionId [ probeItem ] [] None (AgentTurn.FromMessage probe)
                let! events = eventsOf log
                match List.last events with
                | AgentMessageCompleted completed ->
                    // A completed turn also proves `tools: []` leaves the query working.
                    Expect.isFalse (completed.Body.Contains nonce) "no built-in tool could read the host file"
                | AgentTurnFailed f -> failwithf "tool-surface probe turn failed: %s" f.Reason
                | other -> failwithf "expected a completed agent message, got %A" other
            }
    ]


// --- The wake (Plan 20, stage 2) ---------------------------------------------------------

let private blockStarted (n: string) (background: bool) (owner: ActorRef option) =
    SessionEvent.TerminalBlockStarted
        { TerminalId = TerminalId.create "term-a" |> expect
          BlockId = BlockId.create n |> expect
          QueueId = None
          Authority = Authority.rehydrate ActorRef.Agent owner
          Command = "make"
          FromSeq = 0
          Background = background }

let private blockCompleted (n: string) =
    SessionEvent.TerminalBlockCompleted
        { TerminalId = TerminalId.create "term-a" |> expect
          BlockId = BlockId.create n |> expect
          Result = CommandSucceeded 0
          ToSeq = 4 }

let private turnStarted (n: string) =
    AgentTurnStarted
        { AgentTurnId = AgentTurnId.create n |> expect
          TriggeredByMessageId = Some (MessageId.create ("m-" + n) |> expect)
          Woke = None }

let private wakeTests =
    testList "The wake (Plan 20, stage 2)" [

        testCase "a background command that finished owes the agent a turn" <| fun () ->
            Expect.isTrue
                (AgentWake.due [ turnStarted "1"; blockStarted "b1" true (Some (PeerRef ada)); blockCompleted "b1" ])
                "nobody was waiting on it, so somebody has to be told"

        testCase "a foreground command that finished owes nothing" <| fun () ->
            // Somebody WAS waiting: the tool call that queued it is what carries the outcome
            // back, and waking a turn to re-report it would be the second channel this
            // design does not have.
            Expect.isFalse
                (AgentWake.due [ turnStarted "1"; blockStarted "b1" false (Some (PeerRef ada)); blockCompleted "b1" ])
                "its own call answered it"

        testCase "a background command still running owes nothing yet" <| fun () ->
            Expect.isFalse
                (AgentWake.due [ turnStarted "1"; blockStarted "b1" true (Some (PeerRef ada)) ])
                "there is no outcome to be told about"

        testCase "the turn a wake started takes that wake with it" <| fun () ->
            // What makes the wake fire once without storing a cursor: an `AgentTurnStarted`
            // resets the window, exactly as it does for the digest that turn reads.
            Expect.isFalse
                (AgentWake.due [ blockStarted "b1" true (Some (PeerRef ada)); blockCompleted "b1"; turnStarted "woken" ])
                "the turn that was owed has run"

        testCase "a wake names whose turn it is, and it is whoever the work was queued for" <| fun () ->
            // A woken turn has no message to read its authority off, and every turn resolves
            // its repo credential, its sandbox credential and its Claude account from whoever
            // it is FOR. So the block records it and the wake reads it back — continuing the
            // authority the queuing turn had, rather than inventing one.
            Expect.equal
                (AgentWake.pending
                    [ turnStarted "1"; blockStarted "b1" true (Some (PeerRef bob)); blockCompleted "b1" ])
                (Some (PeerRef bob))
                "the party whose turn queued the work"

        testCase "work queued for nobody wakes nobody" <| fun () ->
            // The safe direction, and the one every other reader of `OnBehalfOf` already
            // takes: run on NOTHING rather than on somebody else's credential. Picking the
            // most recent speaker instead would run one person's work as another.
            Expect.isNone
                (AgentWake.pending [ turnStarted "1"; blockStarted "b1" true None; blockCompleted "b1" ])
                "no owner, no turn"

        testCase "several finishing at once are ONE wake, and one turn sees them all" <| fun () ->
            // Coalescing is not a mechanism here, it is a consequence: the wake is a bool
            // over the same window the digest reads, so everything that landed before the
            // turn starts is in that turn's digest.
            let page =
                [ turnStarted "1"
                  blockStarted "b1" true (Some (PeerRef ada))
                  blockStarted "b2" true (Some (PeerRef ada))
                  blockCompleted "b1"
                  blockCompleted "b2" ]
            Expect.isTrue (AgentWake.due page) "one wake"
            Expect.equal
                (Digest.window page |> Set.count)
                2
                "and the turn it starts is told about both"
    ]

// --- The rest of the wake vocabulary (Plan 20, stage 5) ------------------------------------

let private terminalB = TerminalId.create "term-b" |> expect

let private openedIn (id: TerminalId) (sandbox: SandboxRef option) =
    SessionEvent.TerminalOpened
        { TerminalId = id; OpenedBy = ActorRef.Agent; Title = (TerminalTitle.fromProse "work"); Sandbox = sandbox; Renewable = false }

/// A block in a NAMED terminal, so a case can put the agent's work somewhere other than the
/// `term-a` every helper above is pinned to.
let private blockStartedIn (id: TerminalId) (n: string) (background: bool) (owner: ActorRef option) =
    SessionEvent.TerminalBlockStarted
        { TerminalId = id
          BlockId = BlockId.create n |> expect
          QueueId = None
          Authority = Authority.rehydrate ActorRef.Agent owner
          Command = "make"
          FromSeq = 0
          Background = background }

let private integrationLost (id: TerminalId) =
    SessionEvent.TerminalIntegrationLost { TerminalId = id; BlockId = None }

let private closedNow (id: TerminalId) =
    SessionEvent.TerminalClosed { TerminalId = id; Reason = "the source went away" }

let private prWatcher = PeerRef (PeerId.create "ada" |> expect)
let private watchedPr = PrRef.create (RepoRef.create "octo/hello" |> expect) 12 |> expect

let private prWatchStarted =
    SessionEvent.PrWatchStarted
        { MessageId = MessageId.create "w1" |> expect
          Pr = watchedPr
          Initial = { State = PrOpen; Title = "Add feature"; HeadSha = "abc"; Checks = ChecksPending; Mergeable = None }
          Actor = prWatcher }

let private prTransitioned transition =
    SessionEvent.PrTransitioned
        { MessageId = MessageId.create "t1" |> expect
          Pr = watchedPr
          Transition = transition
          State = PrMerged
          Checks = ChecksGreen
          Watcher = prWatcher }

let private prWatchStopped =
    SessionEvent.PrWatchStopped
        { MessageId = MessageId.create "w2" |> expect; Pr = watchedPr; Actor = prWatcher }

let private prWakeTests =
    testList "A watched pull request changing" [

        testCase "a transition owes a turn, as the watcher" <| fun () ->
            // The credential question the roster change fails and this one passes: the
            // watch was an attributed act, and the poll that noticed spent that actor's
            // own credential, so the turn runs as somebody who asked for exactly this.
            Expect.equal
                (AgentWake.pendingReason [ turnStarted "1"; prWatchStarted; prTransitioned PrWasMerged ])
                (Some (PrChanged watchedPr, prWatcher))
                "owed to whoever is watching"

        testCase "a transition before the last turn started owes nothing" <| fun () ->
            // The turn that ran after it already carried the note in its context.
            Expect.equal
                (AgentWake.pendingReason [ prWatchStarted; prTransitioned PrWasMerged; turnStarted "1" ])
                None
                "a new turn takes everything before it"

        testCase "unwatching clears what that pull request owed" <| fun () ->
            // Somebody who has just said they no longer care must not get a turn about it
            // a moment later.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"; prWatchStarted; prTransitioned PrWasMerged; prWatchStopped ])
                None
                "stopped means stopped"

        testCase "a command the agent queued outranks pull request news" <| fun () ->
            // Both are owed; the one the agent itself set running wins, because the other
            // is somebody else's world moving.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      prWatchStarted
                      prTransitioned PrWasMerged
                      blockStartedIn terminalB "b1" true (Some (PeerRef bob))
                      blockCompleted "b1" ]
                 |> Option.map fst)
                (Some CommandFinished)
                "the agent's own work first"

        testCase "the same transition never owes a second turn after a restart" <| fun () ->
            // The restart property, from the wake's side: the debt is what the log records
            // AFTER the last turn start, so re-folding the same log once a turn has run
            // owes nothing however many times the process comes back.
            let log = [ prWatchStarted; prTransitioned PrWasMerged; turnStarted "1" ]
            Expect.equal (AgentWake.pendingReason log) None "already turned on it"
            Expect.equal (AgentWake.pendingReason log) None "and folding again changes nothing"
    ]

let private vocabularyTests =
    testList "The rest of the wake vocabulary (Plan 20, stage 5)" [

        testCase "an attached terminal's stream ending owes the agent a turn" <| fun () ->
            // Nothing else will say so: no tool call of the agent's is still open, and the
            // source it was reading is simply gone.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      openedIn terminalB None
                      blockStartedIn terminalB "b1" false (Some (PeerRef bob))
                      closedNow terminalB ]
                 |> Option.map fst)
                (Some (StreamEnded terminalB))
                "the terminal it names is the one that ended"

        testCase "a SHELL closing owes nothing — that was somebody deciding" <| fun () ->
            // Usually the agent itself, through `close_terminal`. Waking an agent to tell it
            // what it just did would be a loop with a delay in it.
            Expect.isNone
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      openedIn terminalB (Some SandboxRef.defaultRef)
                      blockStartedIn terminalB "b1" false (Some (PeerRef bob))
                      closedNow terminalB ])
                "a sandbox shell is not a stream"

        testCase "a terminal the agent never worked in wakes nothing when it ends" <| fun () ->
            // A source ending under somebody else's terminal is not the agent's news, and
            // there is no turn to continue the authority of.
            Expect.isNone
                (AgentWake.pendingReason [ turnStarted "1"; openedIn terminalB None; closedNow terminalB ])
                "no work there, no turn"

        testCase "an integration lost under the agent's work owes the agent a turn" <| fun () ->
            // The one wake that reports the agent being STUCK: from here nothing can say how
            // that block ended, and the queue behind it is held.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      openedIn terminalB (Some SandboxRef.defaultRef)
                      blockStartedIn terminalB "b1" false (Some (PeerRef bob))
                      integrationLost terminalB ]
                 |> Option.map fst)
                (Some (IntegrationLost terminalB))
                "and it names where"

        testCase "a terminal-shaped wake runs as whoever the agent last worked there for" <| fun () ->
            // `TerminalOpened` records only who ASKED for the terminal, which for the agent's
            // own is the agent — an actor with no credential. So the reason takes the owner of
            // the most recent agent-authored block there: the turn that last did work in this
            // place is the turn this concerns.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      openedIn terminalB (Some SandboxRef.defaultRef)
                      blockStartedIn terminalB "b1" false (Some (PeerRef bob))
                      integrationLost terminalB ]
                 |> Option.map snd)
                (Some (PeerRef bob))
                "the party the work was queued for"

        testCase "being STUCK outranks being told something finished" <| fun () ->
            // They resolve to one turn, and its attribution is the most consequential of
            // them: a held queue changes what the agent should do next, a completion is news.
            // The completion is inside that turn's digest window either way.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      openedIn terminalB (Some SandboxRef.defaultRef)
                      blockStarted "b1" true (Some (PeerRef ada))
                      blockCompleted "b1"
                      blockStartedIn terminalB "b2" false (Some (PeerRef bob))
                      integrationLost terminalB ]
                 |> Option.map fst)
                (Some (IntegrationLost terminalB))
                "the loss wins, however late it arrived"

        testCase "within one kind the FIRST owed still wins" <| fun () ->
            // Precedence is across kinds; it does not reorder what already coalesced.
            Expect.equal
                (AgentWake.pendingReason
                    [ turnStarted "1"
                      blockStarted "b1" true (Some (PeerRef ada))
                      blockStartedIn terminalB "b2" true (Some (PeerRef bob))
                      blockCompleted "b1"
                      SessionEvent.TerminalBlockCompleted
                          { TerminalId = terminalB
                            BlockId = BlockId.create "b2" |> expect
                            Result = CommandSucceeded 0
                            ToSeq = 4 } ]
                 |> Option.map snd)
                (Some (PeerRef ada))
                "the one that was owed first"

        testCase "the turn a terminal-shaped wake started takes that wake with it" <| fun () ->
            // The same window trick every reason rides: an `AgentTurnStarted` resets the
            // debt, so a wake cannot fire twice for one loss.
            Expect.isNone
                (AgentWake.pendingReason
                    [ openedIn terminalB (Some SandboxRef.defaultRef)
                      blockStartedIn terminalB "b1" false (Some (PeerRef bob))
                      integrationLost terminalB
                      turnStarted "woken" ])
                "the turn that was owed has run"

        testCase "who the agent has been in a terminal SURVIVES the turn that established it" <| fun () ->
            // The debt resets at every turn; this does not. It is not something owed, it is
            // who the agent is in that place — and a loss two turns later still has to run as
            // somebody rather than as nobody.
            Expect.equal
                (AgentWake.pendingReason
                    [ openedIn terminalB (Some SandboxRef.defaultRef)
                      blockStartedIn terminalB "b1" false (Some (PeerRef bob))
                      turnStarted "later"
                      integrationLost terminalB ]
                 |> Option.map snd)
                (Some (PeerRef bob))
                "still bob's, one turn on"
    ]

// --- The arm (Plan 20, stage 2): the wake, wired to the scheduler that runs it -------------

/// A scheduler over an in-memory log, with an agent that answers immediately. `duringTurn`
/// runs inside the agent's turn, which is how a completion that lands WHILE the agent is busy
/// is arranged without a clock.
let private appendNow (log: EventLog<SessionEvent>) (event: SessionEvent) =
    // The in-memory log never yields, so this completes before it returns — which is what
    // lets these cases arrange a log and then observe a synchronous scheduler decision.
    Async.StartImmediate (log.Append ActorRef.Agent event |> Async.Ignore)

let private armedScheduler (seed: SessionEvent list) (duringTurn: EventLog<SessionEvent> -> unit) =
    let sessionId = SessionId.create "wake-session" |> expect
    let log = newLog ()
    for event in seed do
        appendNow log event
    let runner : RunAgent =
        fun _ _ _ onChunk ->
            async {
                onChunk { Text = "ok" }
                duringTurn log
                return AgentCompleted ("done", None)
            }
    let mintTurnId =
        let mutable n = 0
        fun () ->
            n <- n + 1
            AgentTurnId.create (sprintf "turn-%d" n) |> expect
    let mintMessageId =
        let mutable n = 0
        fun () ->
            n <- n + 1
            MessageId.create (sprintf "message-%d" n) |> expect
    let scheduler =
        Scheduler.create sessionId (Y.Doc.Create ()) log (fun () -> Some runner)
            (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) mintTurnId mintMessageId PeerRef
            (fun _ _ _ -> []) Set.empty
    scheduler, log

let private startedTurns (log: EventLog<SessionEvent>) =
    async {
        let! events = eventsOf log
        return
            events
            |> List.choose (function AgentTurnStarted started -> Some started | _ -> None)
    }

// A turn's chat item has to carry WHY the turn exists, because an agent that pipes up with
// nobody having spoken is, on the surface people read, indistinguishable from an agent
// deciding things on its own.
let private attributionTests =
    testList "A woken turn, in the chat (Plan 20, stage 2)" [

        let started (woke: WakeReason option) =
            envelope 0L (AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = None; Woke = woke })
        let saidSomething =
            envelope 1L (AgentMessageStarted { AgentTurnId = turnId; MessageId = agentMessageId })

        testCase "what a woken turn says is marked with why the turn exists" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents None [ started (Some CommandFinished); saidSomething ] ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Woke))
                [ Some CommandFinished ]
                "the item says why it is here"

        testCase "what an ordinary turn says is marked with nothing" <| fun () ->
            let projection, _ =
                ConversationProjection.applyEvents None [ started None; saidSomething ] ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Woke))
                [ None ]
                "somebody asked for this one, so there is nothing to explain"

        testCase "a turn that failed before speaking still says why it was running" <| fun () ->
            // The one item a turn can produce without ever starting a message. Unattributed,
            // it reads as the agent failing at something nobody asked it to do — which is
            // exactly the sentence that needs its second half.
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ started (Some CommandFinished)
                      envelope 1L (AgentTurnFailed { AgentTurnId = turnId; Reason = "no credential" }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Woke))
                [ Some CommandFinished ]
                "the failure says why it was running"

        testCase "a message from a turn the wake did not start is not marked by the one that is" <| fun () ->
            // A late `AgentMessageStarted` from the previous turn must not inherit the current
            // turn's reason: the mark says why THIS was said, and a mark that can attach to
            // the wrong item is worse than none.
            let earlier = AgentTurnId.create "turn-earlier" |> expect
            let projection, _ =
                ConversationProjection.applyEvents
                    None
                    [ started (Some CommandFinished)
                      envelope 1L (AgentMessageStarted { AgentTurnId = earlier; MessageId = agentMessageId }) ]
                    ConversationProjection.empty
            Expect.equal
                (projection.Items |> List.map (fun i -> i.Woke))
                [ None ]
                "the mark belongs to the turn that carried it"
    ]

let private armTests =
    testList "The wake, armed (Plan 20, stage 2)" [

        testCaseAsync "a background command that finished starts a turn nobody asked for" <|
            async {
                let scheduler, log =
                    armedScheduler
                        [ blockStarted "b1" true (Some (PeerRef ada)); blockCompleted "b1" ]
                        ignore
                scheduler.Wake ()
                match! startedTurns log with
                | [ started ] ->
                    Expect.equal started.Woke (Some CommandFinished) "and it can say why it exists"
                    Expect.isNone started.TriggeredByMessageId "nobody spoke"
                | other -> failwithf "expected exactly one turn, got %d" (List.length other)
            }

        testCaseAsync "a wake with nothing owed starts nothing" <|
            async {
                // The boot call site fires on every session, most of which owe nothing. It has
                // to be free of consequence, not merely cheap.
                let scheduler, log =
                    armedScheduler [ blockStarted "b1" false (Some (PeerRef ada)); blockCompleted "b1" ] ignore
                scheduler.Wake ()
                let! started = startedTurns log
                Expect.isEmpty started "a foreground command answered its own caller"
            }

        testCaseAsync "a completion that lands while the agent is busy still gets its turn" <|
            async {
                // The terminal drain wakes the moment a block completes, and finds the slot
                // taken. Without a re-read when the turn ends, that debt is collected by
                // nothing: the log keeps it and no call site ever looks again.
                let mutable armed = false
                let scheduler, log =
                    armedScheduler
                        [ blockStarted "b1" true (Some (PeerRef ada)); blockCompleted "b1" ]
                        (fun log ->
                            // Once: a second background command finishing under the running turn.
                            if not armed then
                                armed <- true
                                appendNow log (blockStarted "b2" true (Some (PeerRef ada)))
                                appendNow log (blockCompleted "b2"))
                scheduler.Wake ()
                let! started = startedTurns log
                match started |> List.map (fun t -> t.Woke) with
                | [ Some CommandFinished; Some CommandFinished ] -> ()
                | other -> failwithf "expected the second completion to get its own turn, got %A" other
            }
    ]

/// A scheduler over a doc a PEER writes, so the model choice arrives the way a person's
/// choice actually arrives — through the picker's message and the sync boundary — rather
/// than by a test reaching into the doc with a writer nothing in the product uses.
///
/// Driven by the wake path (a background command that finished) because it needs no
/// composed message queue: what is under test is which model the turn is given, and a turn
/// nobody asked for is still a turn.
let private schedulerOverPickedModel (choice: ModelId option) =
    let doc = Y.Doc.Create ()
    let picker = Harness.run (App.makeProgram doc (ClientModel.init (peer "ada" "Ada")))
    picker.Dispatch (user (SetModelMsg choice))
    let log = newLog ()
    appendNow log (blockStarted "b1" true (Some (PeerRef ada)))
    appendNow log (blockCompleted "b1")
    let mutable seen : ModelId option option = None
    let runner : RunAgent =
        fun context _ _ _ ->
            async {
                seen <- Some context.Model
                return AgentCompleted ("done", None)
            }
    let scheduler =
        Scheduler.create (SessionId.create "model-session" |> expect) doc log (fun () -> Some runner)
            (fun _ _ -> AgentCapabilities.none) (fun _ _ -> ()) (fun () -> turnId) (fun () -> agentMessageId) PeerRef
            (fun _ _ _ -> []) Set.empty
    scheduler, (fun () -> seen)

let private modelChoiceTests =
    testList "The model a turn runs on" [
        testCaseAsync "a turn runs on the model the session picked" <|
            async {
                // The register the picker writes and the one the turn reads are the same
                // register — which is the only thing that makes changing it mid-session mean
                // anything, since nothing is relaunched when somebody does.
                let chosen = ModelId.create "a-model" |> expect
                let scheduler, seen = schedulerOverPickedModel (Some chosen)
                scheduler.Wake ()
                do! waitUntil "the turn ran" (fun () -> (seen ()).IsSome)
                Expect.equal (seen ()) (Some (Some chosen)) "the choice reached the runner"
            }

        testCaseAsync "a session that picked nothing leaves the model to the provider" <|
            async {
                // Not a placeholder id, and not this repo's guess at what is current: no
                // choice is `None`, all the way to the SDK option that is then not passed.
                let scheduler, seen = schedulerOverPickedModel None
                scheduler.Wake ()
                do! waitUntil "the turn ran" (fun () -> (seen ()).IsSome)
                Expect.equal (seen ()) (Some None) "nothing is invented on the session's behalf"
            }
    ]

// -----------------------------------------------------------------------------
// What the SDK threw, and what a person is told it means. The adapter itself needs a
// live model; this is the one decision in it that does not, and it is the decision a
// reader of a stopped turn actually reads.
// -----------------------------------------------------------------------------

let private failureReasonTests =
    testList "The reason a turn stopped" [
        // Verbatim from a real session's event log. The SDK does not YIELD a non-success
        // ending, it throws one, wrapping the CLI's sentence in its own — so the runner's
        // own wording for a step ceiling never once reached a screen, and what a person read
        // named the layer that spoke rather than the thing that happened.
        testCase "the SDK's wrapper comes off, leaving what the CLI said" <| fun () ->
            Expect.equal
                (Yession.Host.Agent.sdkFailureReason
                    "Claude Code returned an error result: Reached maximum number of turns (32)")
                "Reached maximum number of turns (32)"
                "the cause, not the layer that reported it"

        testCase "a reason not wearing the wrapper passes through" <| fun () ->
            Expect.equal
                (Yession.Host.Agent.sdkFailureReason "agent run ended: error_during_execution")
                "agent run ended: error_during_execution"
                "unwrapping is for the wrapper, and nothing else"
    ]

let tests =
    testList "Agent" [
        turnTests
        failureReasonTests
        wakeTests
        modelChoiceTests
        vocabularyTests
        prWakeTests
        attributionTests
        armTests
        Tag.needs "Agent E2E" [ Tag.Ports; Tag.Native ] (fun () -> e2eTests)
        Tag.needs "Agent live SDK" [ Tag.LiveAgent; Tag.Native ] (fun () -> liveTests)
    ]
