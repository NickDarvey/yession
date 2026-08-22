module Yession.Tests.Domain

open System
open Fable.Pyxpecto
open Yession.Domain

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private identityTests =
    testList "Identity smart constructors" [
        testCase "SessionId trims surrounding whitespace" <| fun () ->
            let id = SessionId.create "  session-1  " |> expect
            Expect.equal (SessionId.value id) "session-1" "should be trimmed"

        testCase "SessionId rejects blank input" <| fun () ->
            Expect.isError (SessionId.create "   ") "blank should be rejected"

        testCase "SessionId rejects non-Docker-safe characters" <| fun () ->
            // The id names a container and volume verbatim, so it must be a legal Docker
            // object name: no spaces, slashes, or a leading punctuation char.
            Expect.isError (SessionId.create "bad id") "spaces should be rejected"
            Expect.isError (SessionId.create "bad/id") "slashes should be rejected"
            Expect.isError (SessionId.create "-lead") "leading '-' should be rejected"
            Expect.isError (SessionId.create "x") "a single char is too short for a Docker name"

        testCase "SessionId.mint produces a Docker-safe id that create accepts" <| fun () ->
            let minted = SessionId.mint ()
            let raw = SessionId.value minted
            Expect.equal raw.Length 26 "128 bits Crockford base32-encode to 26 chars"
            let isSafe c =
                (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c = '_' || c = '.' || c = '-'
            Expect.isTrue (String.forall isSafe raw) "every char is a legal Docker name char"
            // Round-trips through the parser (mint and create agree on the invariant).
            Expect.equal (SessionId.create raw |> expect) minted "minted id parses back"

        testCase "SessionId.mint is unique per call" <| fun () ->
            Expect.isFalse (SessionId.mint () = SessionId.mint ()) "two mints differ"

        testCase "PeerId rejects empty input" <| fun () ->
            Expect.isError (PeerId.create "") "empty should be rejected"

        testCase "EventOffset rejects negative values" <| fun () ->
            Expect.isError (EventOffset.create -1L) "negative should be rejected"

        testCase "EventOffset accepts zero" <| fun () ->
            let offset = EventOffset.create 0L |> expect
            Expect.equal (EventOffset.value offset) 0L "zero is valid"

        testCase "EventId rejects the empty guid" <| fun () ->
            Expect.isError (EventId.create Guid.Empty) "empty guid should be rejected"
    ]

let private envelopeSerializationTests =
    let sampleEnvelope () : EventEnvelope<SessionEvent> =
        let sessionId = SessionId.create "session-42" |> expect
        let peerId = PeerId.create "peer-7" |> expect
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = EventOffset.zero
          Actor = PeerRef peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 10, 30, 0, TimeSpan.FromHours 10.0)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    testList "Envelope serialization" [
        testCase "EventEnvelope<SessionEvent> round-trips through serialization unchanged" <| fun () ->
            let original = sampleEnvelope ()
            let json = Codec.toString Codec.sessionEventEnvelope original
            let roundTripped = Codec.fromString Codec.sessionEventEnvelope json |> expect
            Expect.equal roundTripped original "round-trip should be identical"

        testCase "Decoding malformed JSON yields an Error" <| fun () ->
            Expect.isError (Codec.fromString Codec.sessionEventEnvelope "{ not valid json ") "malformed JSON should fail"

        testCase "UserRef actor round-trips through the envelope codec" <| fun () ->
            let user = UserId.create "nick@example.com" |> expect
            let original = { sampleEnvelope () with Actor = UserRef user }
            let json = Codec.toString Codec.sessionEventEnvelope original
            let roundTripped = Codec.fromString Codec.sessionEventEnvelope json |> expect
            Expect.equal roundTripped original "round-trip should be identical"
    ]

let private conversationProjectionTests =
    let sessionId = SessionId.create "session-proj" |> expect

    /// Ordered envelopes with the given offsets, all SessionCreated.
    let envelopes (offsets: int64 list) : EventEnvelope<SessionEvent> list =
        offsets
        |> List.map (fun n ->
            { EventId = EventId.fresh ()
              SessionId = sessionId
              Offset = EventOffset.create n |> expect
              Actor = SessionProcess
              Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
              Event = SessionCreated { SessionCreated.SessionId = sessionId } })

    testList "Conversation projection" [
        testCase "folding a fixed ordered sequence is deterministic" <| fun () ->
            let events = envelopes [ 0L; 1L; 2L; 3L ]
            let first = ConversationProjection.applyEvents None events ConversationProjection.empty
            let second = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal first second "same input yields same projection"

        testCase "high-water offset advances to the last applied offset" <| fun () ->
            let events = envelopes [ 0L; 1L; 2L ]
            let _, highWater = ConversationProjection.applyEvents None events ConversationProjection.empty
            Expect.equal (highWater |> Option.map EventOffset.value) (Some 2L) "high-water is the last offset"

        testCase "re-applying an overlapping page does not advance past the tail or duplicate items" <| fun () ->
            let firstPage = envelopes [ 0L; 1L; 2L ]
            let proj1, hw1 = ConversationProjection.applyEvents None firstPage ConversationProjection.empty
            let overlapping = envelopes [ 1L; 2L; 3L ]
            let proj2, hw2 = ConversationProjection.applyEvents hw1 overlapping proj1
            Expect.equal (hw2 |> Option.map EventOffset.value) (Some 3L) "advances only to 3"
            Expect.equal proj2 proj1 "projection unchanged (no conversation items)"
            Expect.isEmpty proj2.Items "no items contributed"

        testCase "re-applying the identical page is a no-op" <| fun () ->
            let page = envelopes [ 0L; 1L; 2L ]
            let proj1, hw1 = ConversationProjection.applyEvents None page ConversationProjection.empty
            let proj2, hw2 = ConversationProjection.applyEvents hw1 page proj1
            Expect.equal proj2 proj1 "projection unchanged"
            Expect.equal hw2 hw1 "high-water unchanged"
    ]

let private frameSerializationTests =
    let sessionId = SessionId.create "session-frames" |> expect
    let peerId = PeerId.create "peer-1" |> expect
    let requestId = RequestId.fresh ()
    let offset = EventOffset.create 7L |> expect

    let sampleEnvelope : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = sessionId
          Offset = offset
          Actor = PeerRef peerId
          Timestamp = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
          Event = SessionCreated { SessionCreated.SessionId = sessionId } }

    let samplePage : EventPage<SessionEvent> =
        { Events = [ sampleEnvelope ]; LastOffset = Some offset; IsEnd = true }

    let everyVariant : SessionFrame<string> list =
        [ State (StateSync "opaque-sync-payload")
          Command (Request(requestId, InterruptAgentTurn (AgentTurnId.create "turn-1" |> expect)))
          Command (Response(requestId, CommandAccepted))
          Command (Response(requestId, CommandRejected "nope"))
          EventLog (EventsAvailable offset)
          EventLog (ReadEventsAfter(requestId, Some offset, 50))
          EventLog (ReadEventsAfter(requestId, None, 10))
          EventLog (EventsPage(requestId, samplePage))
          Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = "tok" })
          Control (PeerAccepted { SessionId = sessionId; AssignedDisplayName = "Ada"; LatestOffset = Some offset })
          Control (PeerRejected "bad token")
          Control Ping
          Control Pong
          Presence { PeerId = peerId; DisplayName = "Ada"; Focus = Some { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } } }
          Presence { PeerId = peerId; DisplayName = "Ada"; Focus = Some { Field = DraftBody peerId; Pos = { Anchor = "AQI="; Head = "AQI=" } } }
          Presence { PeerId = peerId; DisplayName = "Ada"; Focus = Some { Field = QueueBody (QueueId.create "q-1" |> expect); Pos = { Anchor = "AQI="; Head = "AwQ=" } } }
          Presence { PeerId = peerId; DisplayName = "Ada"; Focus = None } ]

    testList "Session frame serialization" [
        testCase "every session frame variant round-trips unchanged" <| fun () ->
            let codec = Codec.sessionFrame Codec.string
            for frame in everyVariant do
                let roundTripped = Codec.toString codec frame |> Codec.fromString codec |> expect
                Expect.equal roundTripped frame "frame round-trip"

        testCase "PeerJoined and PeerLeft events round-trip through the envelope codec" <| fun () ->
            let joined = { sampleEnvelope with Event = PeerJoined { PeerId = peerId; DisplayName = "Ada"; User = None } }
            let left = { sampleEnvelope with Event = PeerLeft { PeerId = peerId } }
            for env in [ joined; left ] do
                let roundTripped =
                    Codec.toString Codec.sessionEventEnvelope env
                    |> Codec.fromString Codec.sessionEventEnvelope
                    |> expect
                Expect.equal roundTripped env "event round-trip"

        testCase "every SessionEvent case round-trips through the envelope codec" <| fun () ->
            let messageId = MessageId.create "msg-1" |> expect
            let turnId = AgentTurnId.create "turn-1" |> expect
            let toolUseId = ToolUseId.create "tool-1" |> expect
            let blockId = BlockId.create "blk-1" |> expect
            // One value per union case; extending SessionEvent without extending this
            // list is caught by the exhaustive-match warning in the projection instead,
            // so keep the two in step when adding events.
            let everyCase : SessionEvent list =
                [ SessionCreated { SessionCreated.SessionId = sessionId }
                  PeerJoined { PeerId = peerId; DisplayName = "Ada"; User = None }
                  PeerLeft { PeerId = peerId }
                  MessageSent { MessageId = messageId; QueueId = None; Author = PeerRef peerId; Body = "hi" }
                  MessageSent { MessageId = messageId; QueueId = Some (QueueId.create "q-1" |> expect); Author = ActorRef.System; Body = "" }
                  AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = Some (messageId); Woke = None }
                  // Every wake reason (Plan 20, stages 2 and 5). A turn nobody asked for is
                  // the one whose attribution a reader most needs, and a reason that failed
                  // to decode would take the whole page with it — so each rides this list.
                  AgentTurnStarted { AgentTurnId = turnId; TriggeredByMessageId = None; Woke = Some CommandFinished }
                  AgentTurnStarted
                    { AgentTurnId = turnId
                      TriggeredByMessageId = None
                      Woke = Some (StreamEnded (TerminalId.create "term-1" |> expect)) }
                  AgentTurnStarted
                    { AgentTurnId = turnId
                      TriggeredByMessageId = None
                      Woke = Some (IntegrationLost (TerminalId.create "term-1" |> expect)) }
                  AgentContextBuilt { AgentTurnId = turnId; MessageCount = 3 }
                  AgentMessageStarted { AgentTurnId = turnId; MessageId = messageId }
                  AgentMessageDelta { AgentTurnId = turnId; MessageId = messageId; Delta = "d" }
                  AgentMessageCompleted { AgentTurnId = turnId; MessageId = messageId; Body = "done" }
                  AgentTurnFailed { AgentTurnId = turnId; Reason = "overloaded" }
                  AgentTurnInterrupted { AgentTurnId = turnId; RequestedBy = peerId }
                  EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = Some turnId }
                  EnvironmentNeedIdentified { Reason = "task"; AgentTurnId = None }
                  EnvironmentStartRequested { EnvironmentId = "env-1"; SpecSummary = "local-process" }
                  EnvironmentStarted { EnvironmentId = "env-1"; ContainerRef = "ctr-1" }
                  EnvironmentStartFailed { EnvironmentId = "env-1"; Reason = "no image" }
                  EnvironmentStopRequested { EnvironmentId = "env-1" }
                  EnvironmentStopped { EnvironmentId = "env-1" }
                  CommandRequested { CommandId = CommandId.create "cmd-1" |> expect; Executable = "node"; Arguments = [ "-e"; "1" ] }
                  CommandStarted { CommandId = CommandId.create "cmd-1" |> expect }
                  CommandOutputReceived { CommandId = CommandId.create "cmd-1" |> expect; Stream = Stdout; Text = "hi" }
                  CommandOutputReceived { CommandId = CommandId.create "cmd-1" |> expect; Stream = Stderr; Text = "err" }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandSucceeded 0 }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandFailed 3 }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandTimedOut }
                  CommandCompleted { CommandId = CommandId.create "cmd-1" |> expect; Result = CommandExecutionFailed "denied" }
                  RepoAdded { MessageId = messageId; Repo = RepoRef.create "octo/hello" |> expect; Branch = "main"; Actor = PeerRef peerId }
                  RepoRemoved { MessageId = messageId; Repo = RepoRef.create "octo/hello" |> expect; Actor = ActorRef.Agent }
                  RepoBranchSwitched { MessageId = messageId; Repo = RepoRef.create "octo/hello" |> expect; Branch = "feature/x"; Created = true; Actor = UserRef (UserId.create "alice" |> expect) }
                  WorkSandboxStarted
                    { MessageId = messageId
                      Sandbox = SandboxName.create "test" |> expect
                      Backend = "srt"
                      Forwarded = [ "github" ]
                      CredentialOwner = Some (UserRef (UserId.create "alice" |> expect))
                      Actor = ActorRef.Agent }
                  WorkSandboxStarted
                    { MessageId = messageId
                      Sandbox = SandboxName.defaultName
                      Backend = "host"
                      Forwarded = []
                      CredentialOwner = None
                      Actor = PeerRef peerId }
                  WorkSandboxStopped { MessageId = messageId; Sandbox = SandboxName.create "test" |> expect; Actor = ActorRef.Agent }
                  // The shell profile (Plan 25): both cases, because a set and a clear are
                  // one event and the difference between them is the whole payload.
                  ShellProfileSet
                    { MessageId = messageId
                      Sandbox = SandboxName.defaultName
                      WorkingDirectory = Some "/repos/octo/hello"
                      Actor = ActorRef.Agent }
                  ShellProfileSet
                    { MessageId = messageId
                      Sandbox = SandboxName.create "test" |> expect
                      WorkingDirectory = None
                      Actor = PeerRef peerId }
                  // Tool use (Plan 16): both argument cases, because they are different
                  // facts — recorded-with-secrets-gone, and a foreign tool whose arguments
                  // are not recorded at all.
                  ToolUseStarted { ToolUseId = toolUseId; AgentTurnId = turnId; Namespace = "yession"; Name = "set_secret"; Arguments = Some """{"name":"DEPLOY_TOKEN","value":null}""" }
                  ToolUseStarted { ToolUseId = toolUseId; AgentTurnId = turnId; Namespace = "serial"; Name = "acquire_device"; Arguments = None }
                  ToolUseFinished { ToolUseId = toolUseId; Outcome = ToolCallOk; Block = None }
                  ToolUseFinished { ToolUseId = toolUseId; Outcome = ToolCallOk; Block = Some blockId }
                  ToolUseFinished { ToolUseId = toolUseId; Outcome = ToolCallFailed "no such tool"; Block = None }
                  // Plan 17: the two the operator's declarations produce.
                  McpServerAvailable { MessageId = messageId; Name = McpServerName.create "serial" |> expect }
                  McpServerUnavailable { MessageId = messageId; Name = McpServerName.create "printer" |> expect } ]
            for event in everyCase do
                let env = { sampleEnvelope with Event = event }
                let roundTripped =
                    Codec.toString Codec.sessionEventEnvelope env
                    |> Codec.fromString Codec.sessionEventEnvelope
                    |> expect
                Expect.equal roundTripped env "event round-trip"

        testCase "a MessageSent persisted before Phase 3 (no queueId field) still decodes" <| fun () ->
            // Wire compatibility: event-log lines written by earlier versions carry no
            // queueId; they must decode to None, not fail the whole log open.
            let legacy =
                """{"type":"messageSent","payload":{"messageId":"msg-legacy","author":{"kind":"system"},"body":"old line"}}"""
            let decoded = Codec.fromString Codec.sessionEvent legacy |> expect
            Expect.equal
                decoded
                (MessageSent
                    { MessageId = MessageId.create "msg-legacy" |> expect
                      QueueId = None
                      Author = ActorRef.System
                      Body = "old line" })
                "a line without queueId decodes with QueueId = None"
    ]

let private shellProfileTests =
    let sandboxNamed name = SandboxName.create name |> expect
    let profileSet sandbox cwd : SessionEvent =
        ShellProfileSet
            { MessageId = MessageId.create "msg-1" |> expect
              Sandbox = sandbox
              WorkingDirectory = cwd
              Actor = ActorRef.Agent }
    let fold events =
        events |> List.fold ShellProfileProjection.applyEvent ShellProfileProjection.empty
    testList "Shell profile (Plan 25)" [
        testCase "the newest set for a sandbox wins" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxName.defaultName (Some "/repos/one")
                      profileSet SandboxName.defaultName (Some "/repos/two") ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxName.defaultName folded)
                (Some "/repos/two")
                "a profile is replaced, never accumulated"

        testCase "a set leaves the other sandboxes alone" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxName.defaultName (Some "/repos/one")
                      profileSet (sandboxNamed "test") (Some "/repos/two") ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxName.defaultName folded)
                (Some "/repos/one")
                "a path is only a path inside the filesystem that has it"

        testCase "a clear returns its sandbox to no profile" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxName.defaultName (Some "/repos/one")
                      profileSet SandboxName.defaultName None ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxName.defaultName folded)
                None
                "back to wherever the sandbox puts them"

        testCase "a cleared sandbox is not listed at all" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxName.defaultName (Some "/repos/one")
                      profileSet (sandboxNamed "test") (Some "/repos/two")
                      profileSet SandboxName.defaultName None ]
            Expect.equal
                (ShellProfileProjection.listed folded |> List.map (fst >> SandboxName.value))
                [ "test" ]
                "\"has a profile\" and \"starts somewhere\" are one question"

        testCase "a ShellProfileSet on the wire is the shape it has always been" <| fun () ->
            // Pinned as a literal, not round-tripped: a round-trip agrees with whatever the
            // codec currently does, and what a durable log needs is that the codec has not
            // changed under the lines already written.
            let pinned =
                """{"type":"shellProfileSet","payload":{"messageId":"msg-1","sandbox":"default","workingDirectory":"/repos/octo/hello","actor":{"kind":"agent"}}}"""
            Expect.equal
                (Codec.fromString Codec.sessionEvent pinned |> expect)
                (profileSet SandboxName.defaultName (Some "/repos/octo/hello"))
                "the durable form decodes to the event"

        testCase "a profile change reads in the timeline as a sentence" <| fun () ->
            // Everyone in the session is affected — the next terminal a PERSON opens lands
            // there too — and the timeline is the only place they would learn it.
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "session-1" |> expect
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = DateTimeOffset (2026, 8, 22, 0, 0, 0, TimeSpan.Zero)
                  Event = profileSet SandboxName.defaultName (Some "/repos/octo/hello") }
            let proj, _ =
                ConversationProjection.applyEvents None [ envelope ] ConversationProjection.empty
            match proj.Items with
            | [ item ] ->
                Expect.equal item.Body "new terminals in default start in /repos/octo/hello" "the line says where"
                Expect.equal item.Author ActorRef.Agent "attributed to whoever set it"
            | other -> failwithf "expected one act-note, got %A" other

        testCase "a ShellProfileSet with no directory is the clear" <| fun () ->
            let pinned =
                """{"type":"shellProfileSet","payload":{"messageId":"msg-1","sandbox":"default","actor":{"kind":"agent"}}}"""
            Expect.equal
                (Codec.fromString Codec.sessionEvent pinned |> expect)
                (profileSet SandboxName.defaultName None)
                "an absent directory decodes as None rather than failing the log open"
    ]

let private repoTests =
    testList "Repos (Plan 14)" [
        testCase "RepoRef parses owner/repo, trims, and strips a pasted .git" <| fun () ->
            let r = RepoRef.create "  NickDarvey/yession.git  " |> expect
            Expect.equal (RepoRef.owner r) "NickDarvey" "owner"
            Expect.equal (RepoRef.repo r) "yession" ".git stripped"
            Expect.equal (RepoRef.value r) "NickDarvey/yession" "canonical form"
            Expect.equal (RepoRef.cloneUrl r) "https://github.com/NickDarvey/yession.git" "constructed clone url"
            Expect.equal (RepoRef.relativePath r) "NickDarvey/yession" "checkout path"

        testCase "RepoRef refuses everything that is not owner/repo" <| fun () ->
            Expect.isError (RepoRef.create "no-slash") "no slash"
            Expect.isError (RepoRef.create "a/b/c") "two slashes"
            Expect.isError (RepoRef.create "/repo") "empty owner"
            Expect.isError (RepoRef.create "owner/") "empty repo"
            Expect.isError (RepoRef.create "owner/re po") "whitespace inside"
            Expect.isError (RepoRef.create "https://github.com/o/r") "a URL is not a name — the url is constructed, never accepted"
            Expect.isError (RepoRef.create "owner/..") "dot-dot cannot traverse"
            Expect.isError (RepoRef.create (String.replicate 40 "a" + "/repo")) "owner over GitHub's cap"

        testCase "the repos projection folds add, re-add, switch, and remove" <| fun () ->
            let msg n = MessageId.create n |> expect
            let repo = RepoRef.create "octo/hello" |> expect
            let ada = PeerId.create "ada" |> expect
            let folded =
                [ RepoAdded { MessageId = msg "r1"; Repo = repo; Branch = "main"; Actor = PeerRef ada }
                  RepoBranchSwitched { MessageId = msg "r2"; Repo = repo; Branch = "feature/x"; Created = true; Actor = ActorRef.Agent }
                  MessageSent { MessageId = msg "m"; QueueId = None; Author = PeerRef ada; Body = "hi" } ]
                |> List.fold ReposProjection.applyEvent ReposProjection.empty
            Expect.equal folded.Repos [ { Repo = repo; Branch = "feature/x"; AddedBy = PeerRef ada } ] "one repo, on the switched branch"
            let readded = ReposProjection.applyEvent folded (RepoAdded { MessageId = msg "r3"; Repo = repo; Branch = "main"; Actor = ActorRef.Agent })
            Expect.equal readded.Repos [ { Repo = repo; Branch = "main"; AddedBy = ActorRef.Agent } ] "re-add replaces in place"
            let removed = ReposProjection.applyEvent readded (RepoRemoved { MessageId = msg "r4"; Repo = repo; Actor = PeerRef ada })
            Expect.equal removed.Repos [] "removed"

        testCase "repo events fold into the timeline as attributed notes" <| fun () ->
            let msg n = MessageId.create n |> expect
            let repo = RepoRef.create "octo/hello" |> expect
            let sessionId = SessionId.create "repo-session" |> expect
            let ada = PeerId.create "ada" |> expect
            let envelopes =
                [ RepoAdded { MessageId = msg "r1"; Repo = repo; Branch = "main"; Actor = PeerRef ada }
                  RepoBranchSwitched { MessageId = msg "r2"; Repo = repo; Branch = "fix/y"; Created = false; Actor = ActorRef.Agent }
                  RepoRemoved { MessageId = msg "r3"; Repo = repo; Actor = PeerRef ada } ]
                |> List.mapi (fun i event ->
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create (int64 (i + 1)) |> expect
                      Actor = ActorRef.SessionProcess
                      Timestamp = DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero)
                      Event = event })
            let proj, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            Expect.equal (proj.Items |> List.map (fun i -> i.Kind)) [ ConversationItemKind.ActNote; ConversationItemKind.ActNote; ConversationItemKind.ActNote ] "all notes"
            Expect.equal (proj.Items |> List.map (fun i -> i.Body))
                [ "added repo octo/hello (branch main)"; "switched octo/hello to branch fix/y"; "removed repo octo/hello" ]
                "the notes read as sentences"
            Expect.equal (proj.Items |> List.map (fun i -> i.Author)) [ PeerRef ada; ActorRef.Agent; PeerRef ada ] "attributed to the acting party"
    ]

// Who is behind an act (Plan 20). The type exists because these three were loose fields
// every site re-spelled, and one site drifted: agent terminal commands recorded no owner at
// all, so Plan 08's no-borrowing rule held in two places and was absent in a third.
let private authorityTests =
    let ada = PeerId.create "ada" |> expect
    let bob = PeerId.create "bob" |> expect
    testList "Authority (Plan 20)" [

        testCase "a person's act borrows nothing, so it resolves to themselves" <| fun () ->
            let authority = Authority.ofAuthor (PeerRef ada)
            Expect.equal (Authority.onBehalfOf authority) None "there is no authority to state"
            Expect.equal (Authority.effective authority) (PeerRef ada) "and it runs as its own author"

        testCase "an agent's act resolves to the authority it was built with, never to itself" <| fun () ->
            // The rule that went missing, as the only thing `agentFor` can produce: the agent
            // is the acting party and the credential is the turn human's. There is no
            // agent-authored act without one, so the omission would not compile.
            let authority = Authority.agentFor (PeerRef ada)
            Expect.equal (Authority.author authority) ActorRef.Agent "the agent is who acted"
            Expect.equal (Authority.effective authority) (PeerRef ada) "on the turn human's credential"

        testCase "an act recovered without its owner invents no other one" <| fun () ->
            // The decode path's safe direction, and why it does not go through the authoring
            // constructors: a doc entry whose owner did not read back is a fact to recover,
            // not a state to refuse — and refusing it would turn a corrupt field into a
            // missing act. What must never happen is a substitute owner appearing.
            let recovered = Authority.rehydrate ActorRef.Agent None
            Expect.equal (Authority.onBehalfOf recovered) None "no authority is conjured"
            Expect.equal
                (Authority.effective recovered)
                ActorRef.Agent
                "so it resolves to the agent, which has no scope of its own — not to a person"
    ]

/// The model vocabulary: the id's invariant, and the one rule the session's catalogue has
/// — look once, keep the answer, and never keep a failure.
let private modelTests =
    testList "Models" [
        testCase "a model id is trimmed, and refuses what no provider could have named" <| fun () ->
            // This value is handed to a spawned process as an option and arrives from a
            // register any peer may write, so the refusals are the point rather than tidiness.
            Expect.equal
                (ModelId.value (ModelId.create "  a-model  " |> expect))
                "a-model"
                "surrounding whitespace is trimmed"
            Expect.isError (ModelId.create "  ") "blank is not a model"
            Expect.isError (ModelId.create "two words") "inner whitespace is not a model id"
            Expect.isError (ModelId.create "a\nmodel") "nor is a control character"
            Expect.isError (ModelId.create (String.replicate 300 "x")) "nor is a document"

        testCase "a model without a label is offered by its id, never blank" <| fun () ->
            // A picker row with nothing in it reads as a control that failed to load.
            let id = ModelId.create "a-model" |> expect
            Expect.equal (AgentModel.create id "").Name "a-model" "the id stands in for a name"

        testCase "the catalogue is ordered for a person, not for the provider" <| fun () ->
            let of' id name = AgentModel.create (ModelId.create id |> expect) name
            let ordered = ModelCatalogue.ordered [ of' "z" "Beta"; of' "a" "alpha" ]
            Expect.equal
                (ordered |> List.map (fun m -> m.Name))
                [ "alpha"; "Beta" ]
                "by name, case-insensitively"

        testCaseAsync "a successful lookup happens once and is kept for the session" <|
            async {
                let mutable asked = 0
                let cached =
                    ModelCatalogue.cached (fun _ ->
                        async {
                            asked <- asked + 1
                            return Ok [ AgentModel.create (ModelId.create "a-model" |> expect) "A" ]
                        })
                let! first = cached ActorRef.Agent
                let! second = cached ActorRef.Agent
                Expect.equal asked 1 "the provider is asked once"
                Expect.equal second first "and every later reader gets the same answer"
            }

        testCaseAsync "a failed lookup is not kept, so signing in later fills the picker" <|
            async {
                // The failure a session actually hits is "nothing is connected yet", and the
                // remedy happens in the panel above the picker. Caching that would leave the
                // picker permanently empty for a session that fixes it a minute later.
                let mutable asked = 0
                let cached =
                    ModelCatalogue.cached (fun _ ->
                        async {
                            asked <- asked + 1
                            if asked = 1 then return Error "not connected"
                            else return Ok [ AgentModel.create (ModelId.create "a-model" |> expect) "A" ]
                        })
                let! failed = cached ActorRef.Agent
                Expect.isError failed "the first ask reports why it could not"
                let! second = cached ActorRef.Agent
                Expect.isOk second "and the next ask tries again"
            }
    ]

let tests =
    testList "Domain" [
        identityTests
        modelTests
        authorityTests
        envelopeSerializationTests
        conversationProjectionTests
        repoTests
        shellProfileTests
        frameSerializationTests
    ]
