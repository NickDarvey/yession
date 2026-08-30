module Yession.Tests.Domain

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Tools
open Yession.Domain.Sandboxes
open Yession.Domain.Agent
open Yession.Domain.Link
open Yession.Domain.Repos
open Yession.Domain.Prs
open Yession.Domain.Chat
open Yession.Domain.Hooks

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
                      Sandbox = SandboxRef.parse "test" |> expect
                      Backend = "srt"
                      Forwarded = [ "github" ]
                      CredentialOwner = Some (UserRef (UserId.create "alice" |> expect))
                      Realisation = [ "the socket at /run/docker.sock — this host cannot scope that" ]
                      Actor = ActorRef.Agent }
                  WorkSandboxStarted
                    { MessageId = messageId
                      Sandbox = SandboxRef.defaultRef
                      Backend = "host"
                      Forwarded = []
                      CredentialOwner = None
                      Realisation = []
                      Actor = PeerRef peerId }
                  WorkSandboxStopped { MessageId = messageId; Sandbox = SandboxRef.parse "test" |> expect; Actor = ActorRef.Agent }
                  // The shell profile (Plan 25): both cases, because a set and a clear are
                  // one event and the difference between them is the whole payload.
                  ShellProfileSet
                    { MessageId = messageId
                      Sandbox = SandboxRef.defaultRef
                      WorkingDirectory = Some "/repos/octo/hello"
                      Actor = ActorRef.Agent }
                  ShellProfileSet
                    { MessageId = messageId
                      Sandbox = SandboxRef.parse "test" |> expect
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
                  McpServerUnavailable { MessageId = messageId; Name = McpServerName.create "printer" |> expect }
                  // Watched pull requests: a start (with its baseline snapshot), a stop,
                  // and a transition — including the optional-mergeable both ways.
                  PrWatched
                    { MessageId = messageId
                      Pr = { Repo = RepoRef.create "octo/hello" |> expect; Number = 12 }
                      Initial =
                        { State = PrOpen
                          Title = "Add feature"
                          HeadSha = "abc123"
                          Checks = ChecksPending
                          Queued = true
                          Mergeable = Some true }
                      Actor = PeerRef peerId }
                  PrWatched
                    { MessageId = messageId
                      Pr = { Repo = RepoRef.create "octo/hello" |> expect; Number = 13 }
                      Initial =
                        { State = PrClosed
                          Title = "Old"
                          HeadSha = "def456"
                          Checks = ChecksNone
                          Queued = false
                          Mergeable = None }
                      Actor = ActorRef.Agent }
                  PrUnwatched
                    { MessageId = messageId
                      Pr = { Repo = RepoRef.create "octo/hello" |> expect; Number = 12 }
                      Actor = PeerRef peerId }
                  PrTransitioned
                    { MessageId = messageId
                      Pr = { Repo = RepoRef.create "octo/hello" |> expect; Number = 12 }
                      Transition = PrTransition.ChecksFailed
                      State = PrOpen
                      Checks = ChecksRed
                      Watcher = PeerRef peerId } ]
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

        testCase "a sandbox persisted before repos could declare one still decodes" <| fun () ->
            // The scope rides in the SAME string rather than a new field, so a log written
            // when every sandbox was the session's own reads back as session-owned — the
            // compatibility is a property of the wire form, not a branch in the decoder.
            let legacy =
                """{"type":"workSandboxStarted","payload":{"messageId":"msg-legacy","sandbox":"build","backend":"srt","forwarded":[],"credentialOwner":null,"actor":{"kind":"agent"}}}"""
            let decoded = Codec.fromString Codec.sessionEvent legacy |> expect
            Expect.equal
                decoded
                (WorkSandboxStarted
                    { MessageId = MessageId.create "msg-legacy" |> expect
                      Sandbox = SandboxRef.create SessionOwned (SandboxName.create "build" |> expect)
                      Backend = "srt"
                      Forwarded = []
                      CredentialOwner = None
                      // A start recorded before the host became an author of a grant. Absent
                      // reads as "nothing was measured", which is the only honest answer for
                      // a sandbox nobody asked the question about.
                      Realisation = []
                      Actor = ActorRef.Agent })
                "a bare name is the sandbox the session itself owns"
    ]

let private shellProfileTests =
    let sandboxNamed name = SandboxRef.create SessionOwned (SandboxName.create name |> expect)
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
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet SandboxRef.defaultRef (Some "/repos/two") ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxRef.defaultRef folded)
                (Some "/repos/two")
                "a profile is replaced, never accumulated"

        testCase "a set leaves the other sandboxes alone" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet (sandboxNamed "test") (Some "/repos/two") ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxRef.defaultRef folded)
                (Some "/repos/one")
                "a path is only a path inside the filesystem that has it"

        testCase "a clear returns its sandbox to no profile" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet SandboxRef.defaultRef None ]
            Expect.equal
                (ShellProfileProjection.workingDirectory SandboxRef.defaultRef folded)
                None
                "back to wherever the sandbox puts them"

        testCase "a cleared sandbox is not listed at all" <| fun () ->
            let folded =
                fold
                    [ profileSet SandboxRef.defaultRef (Some "/repos/one")
                      profileSet (sandboxNamed "test") (Some "/repos/two")
                      profileSet SandboxRef.defaultRef None ]
            Expect.equal
                (ShellProfileProjection.listed folded |> List.map (fst >> SandboxRef.render))
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
                (profileSet SandboxRef.defaultRef (Some "/repos/octo/hello"))
                "the durable form decodes to the event"

        testCase "a directory under a tree is inside it, and so is the tree itself" <| fun () ->
            // What a caller deleting a checkout asks about every profile it might invalidate.
            Expect.isTrue (ShellProfile.isInside "/repos/octo/hello" "/repos/octo/hello") "the tree itself"
            Expect.isTrue (ShellProfile.isInside "/repos/octo/hello" "/repos/octo/hello/src") "and anything under it"
            Expect.isTrue (ShellProfile.isInside "/repos/octo/hello/" "/repos/octo/hello") "however either side spells a directory"

        testCase "a sibling that merely shares a prefix is not inside it" <| fun () ->
            // The bug a bare `StartsWith` has, and the reason this is a function rather than
            // an inline test at the caller: `/repos/octo/hello-world` is a different checkout,
            // and clearing its profile because `/repos/octo/hello` went would be silent.
            Expect.isFalse
                (ShellProfile.isInside "/repos/octo/hello" "/repos/octo/hello-world")
                "a prefix is not a parent unless it ends on a directory boundary"
            Expect.isFalse (ShellProfile.isInside "/repos/octo/hello" "/repos/octo") "nor is the parent inside the child"

        testCase "a profile change reads in the timeline as a sentence" <| fun () ->
            // Everyone in the session is affected — the next terminal a PERSON opens lands
            // there too — and the timeline is the only place they would learn it.
            let envelope : EventEnvelope<SessionEvent> =
                { EventId = EventId.fresh ()
                  SessionId = SessionId.create "session-1" |> expect
                  Offset = EventOffset.create 1L |> expect
                  Actor = ActorRef.Agent
                  Timestamp = DateTimeOffset (2026, 8, 22, 0, 0, 0, TimeSpan.Zero)
                  Event = profileSet SandboxRef.defaultRef (Some "/repos/octo/hello") }
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
                (profileSet SandboxRef.defaultRef None)
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

let private prWatchTests =
    let msg n = MessageId.create n |> expect
    let repo = RepoRef.create "octo/hello" |> expect
    let pr = PrRef.create repo 12 |> expect
    let ada = PeerId.create "ada" |> expect
    let snapshotOf state checks queued : PrSnapshot =
        { State = state; Title = "Add feature"; HeadSha = "abc123"; Checks = checks; Queued = queued; Mergeable = None }
    let snapshot state checks : PrSnapshot = snapshotOf state checks false
    /// The baseline as a watch that has never seen a queue reads it.
    let known state checks : PrKnown = { State = state; Checks = checks; Queue = NotQueued }
    /// ...and as one that has: auto merge armed, the last thing anybody was told.
    let queued state checks : PrKnown = { State = state; Checks = checks; Queue = Queued }
    let started state checks : SessionEvent =
        PrWatched { MessageId = msg "w1"; Pr = pr; Initial = snapshot state checks; Actor = PeerRef ada }
    let transitioned transition state checks : SessionEvent =
        PrTransitioned
            { MessageId = msg "t1"; Pr = pr; Transition = transition; State = state; Checks = checks; Watcher = PeerRef ada }
    /// The projection folds ENVELOPES, because when a watch last moved is the envelope's
    /// timestamp and nothing in a payload says it. Minute-apart stamps, so a test can tell
    /// which event a `Since` came from.
    let at (minute: int) (event: SessionEvent) : EventEnvelope<SessionEvent> =
        { EventId = EventId.fresh ()
          SessionId = SessionId.create "pr-session" |> expect
          Offset = EventOffset.create 1L |> expect
          Actor = ActorRef.System
          Timestamp = DateTimeOffset (2026, 8, 27, 10, minute, 0, TimeSpan.Zero)
          Event = event }
    let fold envelopes = envelopes |> List.fold PrWatchesProjection.applyEvent PrWatchesProjection.empty

    testList "Watched pull requests" [
        testCase "PrRef parses a number and renders canonically" <| fun () ->
            Expect.equal (PrRef.render pr) "octo/hello#12" "canonical rendering"
            Expect.isError (PrRef.create repo 0) "zero is not a PR number"
            Expect.isError (PrRef.create repo -3) "nor is a negative"

        testCase "a merge, a close and a reopen are each one transition" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ] "open to merged"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksNone) (snapshot PrClosed ChecksNone))
                [ PrTransition.Closed ] "open to closed"
            Expect.equal
                (PrTransitions.detect (known PrClosed ChecksNone) (snapshot PrOpen ChecksNone))
                [ PrTransition.Reopened ] "closed to open"
            Expect.equal
                (PrTransitions.detect (known PrClosed ChecksNone) (snapshot PrMerged ChecksNone))
                [ PrTransition.Merged ] "a closed baseline learning of a merge is a merge"

        testCase "checks arriving at green or red are news; entering pending is not" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksPending) (snapshot PrOpen ChecksGreen))
                [ PrTransition.ChecksPassed ] "pending to green"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksRed))
                [ PrTransition.ChecksFailed ] "green to red"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksPending))
                [] "a new push resetting checks is the rhythm of work, not news"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksGreen))
                [] "no movement, no news"

        testCase "a merge and a green arriving together announce both, state first" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksPending) (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ] "checks on a PR that just left open are not announced"
            Expect.equal
                (PrTransitions.detect (known PrClosed ChecksPending) (snapshot PrOpen ChecksGreen))
                [ PrTransition.Reopened; PrTransition.ChecksPassed ] "a reopen makes its checks news again, state first"

        testCase "checks movement on a merged baseline is suppressed" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrMerged ChecksGreen) (snapshot PrMerged ChecksRed))
                [] "CI going red on a merged PR is not actionable from here"

        testCase "auto merge arming is announced once" <| fun () ->
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshotOf PrOpen ChecksGreen true))
                [ PrTransition.Queued ] "it is on its way in with nobody needed"
            Expect.equal
                (PrTransitions.detect (queued PrOpen ChecksGreen) (snapshotOf PrOpen ChecksGreen true))
                [] "and saying so again on every poll would be noise"

        testCase "auto merge disarming on an open pull request is a stall" <| fun () ->
            // What a merge queue ejecting an entry looks like from outside: the state does
            // not move, the checks do not move, it just stops being on its way in.
            Expect.equal
                (PrTransitions.detect (queued PrOpen ChecksGreen) (snapshot PrOpen ChecksGreen))
                [ PrTransition.Stalled ] "somebody has to re-arm it"
            Expect.equal
                (PrTransitions.detect (known PrOpen ChecksGreen) (snapshot PrOpen ChecksGreen))
                [] "a pull request that was never queued has not stalled"

        testCase "a re-armed pull request is queued again" <| fun () ->
            let stalled = PrTransitions.advance (queued PrOpen ChecksGreen) PrTransition.Stalled
            Expect.equal
                (PrTransitions.detect stalled (snapshotOf PrOpen ChecksGreen true))
                [ PrTransition.Queued ] "it is again true that nobody is needed"

        testCase "a queued pull request that merges is not also reported stalled" <| fun () ->
            // It left the queue by going through it. Reporting that as a stall would file
            // the success as a failure.
            Expect.equal
                (PrTransitions.detect (queued PrOpen ChecksGreen) (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ] "the merge is the whole news"

        testCase "a status word is the last thing that happened, worst first" <| fun () ->
            Expect.equal (PrStatus.word Queued PrOpen) "queued" "armed and waiting on machines"
            Expect.equal (PrStatus.word Stalled PrOpen) "stalled" "nobody driving"
            Expect.equal (PrStatus.word NotQueued PrOpen) "open" "the ordinary state"
            Expect.equal (PrStatus.word Queued PrMerged) "merged" "a merged PR has stopped caring what a queue thought"
            Expect.equal (PrStatus.word Queued PrClosed) "closed" "and so has a closed one"
            Expect.equal (PrStatus.worse "queued" "stalled") "stalled" "stalled wants a person more than queued"
            Expect.equal (PrStatus.worse "merged" "open") "open" "an open PR is still owed; a merged one is not"
            Expect.equal (PrStatus.worse "merged" "a word from the future") "merged" "an unknown word does not shout"

        testCase "a summary of no live pull requests says nothing at all" <| fun () ->
            // Silence is the feature. A roster line that is THERE is worth reading only
            // because a session with nothing owed does not print one.
            let merged n = PrRef.create repo n |> expect, "merged"
            Expect.equal (PrStatus.summarize []) "" "nothing watched"
            Expect.equal (PrStatus.summarize [ merged 1; merged 2 ]) "" "and nothing still owed"

        testCase "a summary names a lone pull request and counts a crowd, worst first" <| fun () ->
            let at n word = PrRef.create repo n |> expect, word
            Expect.equal (PrStatus.summarize [ at 377 "queued" ]) "#377 queued" "one is named"
            Expect.equal
                (PrStatus.summarize [ at 1 "queued"; at 2 "stalled"; at 3 "queued" ])
                "3 PRs · 1 stalled"
                "several are counted, and the count that follows is of the worst"
            Expect.equal
                (PrStatus.summarize [ at 1 "queued"; at 2 "merged"; at 3 "open" ])
                "2 PRs · 1 open"
                "a merged one is history and is not among them"
            Expect.equal
                (PrStatus.summarize [ at 1 "stalled"; at 2 PrStatus.unreachable ])
                "2 PRs · 1 unreachable"
                "a watch that cannot be read is worse news than one that stalled"

        testCase "the watches projection folds start, re-watch, transition and stop" <| fun () ->
            let folded = fold [ at 0 (started PrOpen ChecksPending) ]
            Expect.equal
                folded.Watches
                [ { Pr = pr
                    Watcher = PeerRef ada
                    Known = (known PrOpen ChecksPending)
                    Since = DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero) } ]
                "a watch starts from its Initial baseline"
            let advanced =
                PrWatchesProjection.applyEvent folded (at 5 (transitioned PrTransition.ChecksPassed PrOpen ChecksGreen))
            Expect.equal
                (PrWatchesProjection.tryFind pr advanced |> Option.map (fun w -> w.Known))
                (Some (known PrOpen ChecksGreen))
                "a recorded transition advances the baseline"
            let rewatched =
                PrWatchesProjection.applyEvent
                    advanced
                    (at
                        9
                        (PrWatched
                            { MessageId = msg "w2"; Pr = pr; Initial = snapshot PrOpen ChecksNone; Actor = ActorRef.Agent }))
            Expect.equal
                rewatched.Watches
                [ { Pr = pr
                    Watcher = ActorRef.Agent
                    Known = (known PrOpen ChecksNone)
                    Since = DateTimeOffset (2026, 8, 27, 10, 9, 0, TimeSpan.Zero) } ]
                "re-watch replaces in place, newest baseline and watcher win"
            let stopped =
                PrWatchesProjection.applyEvent
                    rewatched
                    (at 12 (PrUnwatched { MessageId = msg "w3"; Pr = pr; Actor = PeerRef ada }))
            Expect.equal stopped.Watches [] "stopped"

        testCase "a watch dates itself from the last thing that was recorded about it" <| fun () ->
            // What separates a suite still working from one that died. It moves on an
            // EVENT and never on a look: a poll that found nothing new has learned nothing
            // about when this pull request became what it is.
            let started = fold [ at 0 (started PrOpen ChecksPending) ]
            let since (proj: PrWatchesProjection) =
                PrWatchesProjection.tryFind pr proj |> Option.map (fun w -> w.Since)
            Expect.equal
                (since started)
                (Some (DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero)))
                "a fresh watch dates from when it began"
            let moved =
                PrWatchesProjection.applyEvent started (at 7 (transitioned PrTransition.ChecksPassed PrOpen ChecksGreen))
            Expect.equal
                (since moved)
                (Some (DateTimeOffset (2026, 8, 27, 10, 7, 0, TimeSpan.Zero)))
                "and re-dates from each transition after it"

        testCase "a restart re-announces nothing: the folded baseline already knows what was said" <| fun () ->
            // The dedupe property the durable baseline exists for. After a green was
            // recorded, folding the same log and comparing against the same green
            // snapshot detects nothing — however many times the process restarts.
            let folded =
                fold
                    [ at 0 (started PrOpen ChecksPending)
                      at 5 (transitioned PrTransition.ChecksPassed PrOpen ChecksGreen) ]
            let known = (PrWatchesProjection.tryFind pr folded |> Option.get).Known
            Expect.equal (PrTransitions.detect known (snapshot PrOpen ChecksGreen)) [] "already announced"
            Expect.equal
                (PrTransitions.detect known (snapshot PrMerged ChecksGreen))
                [ PrTransition.Merged ]
                "while a change that happened during the downtime is still detected"

        testCase "watch events read in the timeline as attributed notes" <| fun () ->
            let sessionId = SessionId.create "pr-session" |> expect
            let envelopes =
                [ PeerRef ada, started PrOpen ChecksPending
                  ActorRef.System, transitioned PrTransition.Merged PrMerged ChecksGreen
                  PeerRef ada, PrUnwatched { MessageId = msg "w9"; Pr = pr; Actor = PeerRef ada } ]
                |> List.mapi (fun i (actor, event) ->
                    { EventId = EventId.fresh ()
                      SessionId = sessionId
                      Offset = EventOffset.create (int64 (i + 1)) |> expect
                      Actor = actor
                      Timestamp = DateTimeOffset (2026, 8, 27, 10, 0, 0, TimeSpan.Zero)
                      Event = event })
            let proj, _ = ConversationProjection.applyEvents None envelopes ConversationProjection.empty
            Expect.equal
                (proj.Items |> List.map (fun i -> i.Body))
                [ "PR octo/hello#12 watched (open, checks pending)"
                  "PR octo/hello#12 merged"
                  "PR octo/hello#12 unwatched" ]
                "the notes read as sentences"
            Expect.equal
                (proj.Items |> List.map (fun i -> i.Author))
                [ PeerRef ada; PeerRef ada; PeerRef ada ]
                "a transition wears the watcher's name, not System's"
            Expect.equal
                (proj.Items |> List.map (fun i -> i.Kind))
                [ ConversationItemKind.ActNote; ConversationItemKind.ActNote; ConversationItemKind.ActNote ]
                "all notes"

        testCase "a PrTransitioned on the wire is the shape it will always be" <| fun () ->
            // Pinned as a literal, not round-tripped: what a durable log needs is that the
            // codec has not changed under the lines already written.
            let pinned =
                """{"type":"prTransitioned","payload":{"messageId":"t1","pr":{"repo":"octo/hello","number":12},"transition":"merged","state":"merged","checks":"green","watcher":{"kind":"peer","peerId":"ada"}}}"""
            Expect.equal
                (Codec.fromString Codec.sessionEvent pinned |> expect)
                (transitioned PrTransition.Merged PrMerged ChecksGreen)
                "the durable form decodes to the event"
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

/// The spawn contract (Plan 27): one envelope, minted by the Manager, decoded once by the
/// session. Each case pins one promise of that contract.
let private launchTests =
    testList "Launch envelope (Plan 27)" [
        testCase "a launch round-trips through the variable" <| fun () ->
            let launch =
                { Session = SessionId.create "sess-1" |> expect
                  DataDir = "/data/sess-1"
                  Port = 0
                  Control = Some { Url = "http://127.0.0.1:8321"; Secret = "s3cret" }
                  ParentGuard = true }
            Expect.equal (Launch.parse (Launch.encode launch)) (Ok launch) "what the Manager mints is what the session reads"

        testCase "a launch with no Manager round-trips too" <| fun () ->
            // The unsupervised shape has to survive the wire, not just the default: a
            // session spawned without a control leg is an ordinary session.
            let launch = { Launch.unlaunched with Session = SessionId.create "sess-2" |> expect }
            Expect.equal (Launch.parse (Launch.encode launch)) (Ok launch) "an absent control leg decodes as None"

        testCase "an absent variable is the unlaunched session, not an error" <| fun () ->
            // `yession-session` run by hand still runs.
            Expect.equal (Launch.parse "") (Ok Launch.unlaunched) "blank means nobody launched us"

        testCase "an unlaunched session has no Manager to report to" <| fun () ->
            Expect.equal Launch.unlaunched.Control None "there is nothing to authenticate against"

        testCase "an unlaunched session does not die with a parent it never had" <| fun () ->
            Expect.isFalse Launch.unlaunched.ParentGuard "nothing closes its stdin"

        testCase "a malformed envelope fails the boot" <| fun () ->
            // The defect this whole shape exists to stop: the old code fabricated a session
            // id when the variable was missing a field, so a launch that forgot to say who
            // it was booted anyway, as somebody else.
            Expect.isError (Launch.parse "{\"dataDir\":\"/d\",\"port\":0}") "a launch with no session id is a contract disagreement"

        testCase "an envelope naming an unusable session id fails the boot" <| fun () ->
            // The id names a container and a volume verbatim, so the decoder holds
            // `SessionId`'s rule rather than deferring it to a later, less legible failure.
            Expect.isError
                (Launch.parse "{\"session\":\"bad id\",\"dataDir\":\"/d\",\"port\":0}")
                "a non-Docker-safe id is refused where it arrives"

        testCase "the launch variable is not something a repo may author" <| fun () ->
            // It carries the control secret. `Sandboxes.hostBaseline` is an allowlist, so a
            // sandboxed command never sees it (Phase2); this pins the other half — the name
            // is under the reserved prefix `yession.yaml` refuses.
            Expect.isTrue (Launch.Variable.StartsWith "YESSION_") "it lives under the reserved prefix"
    ]

// -----------------------------------------------------------------------------
// `yession.yaml` (Plan 27): the vocabulary, the decoder and the algebra.
//
// Driven from JSON literals rather than YAML, because YAML is a superset of JSON and the
// decoder is parser-free — which is what lets every one of these run on BOTH runtimes in
// the cheap tier, with no dependency on whatever reads the file off disk.
// -----------------------------------------------------------------------------

let private repo (raw: string) = RepoRef.create raw |> expect
let private sandboxName (raw: string) = SandboxName.create raw |> expect
let private configTests =
    testList "yession.yaml (Plan 27)" [

        testCase "a declaration carries what start_work_sandbox can already be told" <| fun () ->
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container": { "image": "node:24", "cmd": "npm start" },
                          "workdir": "./app",
                          "env": { "NODE_ENV": "development" },
                          "uses": [ "npm" ],
                          "files": { ".config/tool/first-run": "" },
                          "forward": [ "github" ] } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            let container = dev.Container |> Option.get
            Expect.equal container.Image (Some { Name = "node"; Tag = Some "24" }) "the image splits on its tag"
            Expect.equal dev.WorkingDirectory (Some "./app") "the workdir is the repo's own"
            Expect.equal container.Command (Some "npm start") "the sandbox's process"
            Expect.equal (dev.Uses |> List.map ResourceName.value) [ "npm" ] "the resources it selects"
            Expect.equal dev.Forward [ "github" ] "the credentials by name"

        // A file is authored by whoever can push to the repo, so a path it writes is that
        // author naming a place on somebody else's machine. Refused where it is WRITTEN,
        // because that is where the person who can fix it is standing.
        testCase "a workdir that leaves the checkout is refused, and says which way" <| fun () ->
            let refusal workdir =
                match ConfigFile.parse (sprintf """{ "version": 2, "sandboxes": { "dev": { "workdir": "%s" } } }""" workdir) with
                | Ok _ -> failwithf "expected a refusal for '%s'" workdir
                | Error e -> e
            Expect.isTrue ((refusal "/etc").Contains "absolute") "an absolute path names another machine's tree"
            Expect.isTrue ((refusal "../elsewhere").Contains "climbs out") "and a relative one can mean the same thing"
            Expect.isTrue ((refusal "app/../../elsewhere").Contains "climbs out") "wherever the segment sits"

        // A seeded file is the one thing a repo writes that is not a name, and it is safe
        // ONLY while it cannot leave the home this session made for that sandbox. So
        // leaving is what is refused, in every spelling that means it.
        testCase "a seeded file that leaves the sandbox's home is refused, in every spelling" <| fun () ->
            let refusal path =
                match ConfigFile.parse (sprintf """{ "version": 2, "sandboxes": { "dev": { "files": { "%s": "x" } } } }""" path) with
                | Ok _ -> failwithf "expected a refusal for '%s'" path
                | Error e -> e
            Expect.isTrue ((refusal "/etc/passwd").Contains "absolute") "an absolute path is another tree"
            Expect.isTrue ((refusal "../escape").Contains "outside") "and so is climbing out"
            Expect.isTrue ((refusal "a/../../escape").Contains "outside") "wherever the segment sits"
            Expect.isTrue ((refusal "a//b").Contains "empty") "an empty segment could mean either"
            Expect.isTrue ((refusal "dir/").Contains "file name") "and a directory is not a file"

        // The other half: what a repo legitimately asks for survives to the spec that
        // materialises it. Without this the refusals above could pass with the feature
        // decoding to nothing at all.
        testCase "a seeded file reaches the spec the sandbox is built from" <| fun () ->
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": { "dev": { "files": { ".local/share/NuGet/Migrations/1": "" } } } }"""
                |> expect
            let decl = file.Sandboxes |> Map.find (sandboxName "dev")
            let request = SandboxDecl.toRequest (Some "/repos/octo/hello") decl |> expect
            Expect.equal
                (request.Spec.Files |> Map.toList |> List.map (fun (path, content) -> HomePath.value path, content))
                [ ".local/share/NuGet/Migrations/1", "" ]
                "the path and its content, as written"

        // `HostPath` exists and the SESSION uses it — that is how the repos directory reaches
        // a container. A source a repo could name is arbitrary access to the machine running
        // the session, which is the authority the reserved prefix already refuses.
        testCase "a volume may not name a host path" <| fun () ->
            let volume source =
                ConfigFile.parse (
                    sprintf
                        """{ "version": 2, "sandboxes": { "dev": { "container": { "volumes": [ { "source": "%s", "target": "/w" } ] } } } }"""
                        source)
            match volume "/var/run/docker.sock" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "host path") "it says what the source is"
                Expect.isTrue (e.Contains "workspace") "and what a file may say instead"
            let mountsOf file =
                (file |> expect : ConfigFile).Sandboxes
                |> Map.find (sandboxName "dev")
                |> fun decl -> (decl.Container |> Option.get).Mounts |> List.map (fun m -> m.Source)
            Expect.equal (mountsOf (volume "workspace")) [ SessionWorkspace ] "its own checkout, by name"
            Expect.equal (mountsOf (volume "cache")) [ NamedVolume "cache" ] "or a volume the session owns"

        // The file's whole claim: it says nothing a command could not be told. So what a
        // declaration becomes is the ask itself, with the one thing a file cannot know —
        // where the session put the checkout — filled in.
        testCase "a declaration becomes the ask, against this session's checkout" <| fun () ->
            let decl =
                (ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container": { "image": "node:24" },
                          "workdir": "./app",
                          "env": { "NODE_ENV": "development" },
                          "uses": [ "npm" ],
                          "uses": [ "npm" ],
                          "forward": [ "github" ] } } }"""
                 |> expect).Sandboxes
                |> Map.find (sandboxName "dev")
            let request = SandboxDecl.toRequest (Some "/data/repos/octo/hello") decl |> expect
            Expect.equal request.Spec.WorkingDirectory (Some "/data/repos/octo/hello/app") "the workdir is under the checkout"
            Expect.equal (request.Spec.Uses |> List.map ResourceName.value) [ "npm" ] "the resources it selects"
            Expect.equal request.Forward [ "github" ] "the credentials by name"
            Expect.equal
                request.Spec.Runtime
                (Container { ContainerSpec.defaults with Image = Some { Name = "node"; Tag = Some "24" } })
                "and the container it named"

        testCase "the checkout itself is a workdir, however it is written" <| fun () ->
            for written in [ "."; "./"; "app/.." ] do
                let decl = { SandboxDecl.empty with WorkingDirectory = Some written }
                Expect.equal
                    (SandboxDecl.toRequest (Some "/data/repos/octo/hello") decl |> expect).Spec.WorkingDirectory
                    (Some "/data/repos/octo/hello")
                    (sprintf "'%s' is the checkout" written)

        // The downstream half of the workdir rule. The decoder refuses a climbing path where
        // a person can fix it; this is what a declaration arriving some other way still
        // cannot do, because the arithmetic here has no answer above the checkout: climbing
        // is CLAMPED there, so `../../etc` names the same directory `etc` does.
        testCase "no declaration can name a directory above its checkout" <| fun () ->
            let resolved written =
                (SandboxDecl.toRequest (Some "/data/repos/octo/hello") { SandboxDecl.empty with WorkingDirectory = Some written } |> expect)
                    .Spec.WorkingDirectory
            Expect.equal (resolved "../..") (Some "/data/repos/octo/hello") "climbing runs out at the checkout"
            Expect.equal (resolved "../../etc") (Some "/data/repos/octo/hello/etc") "and what follows lands inside it"
            Expect.equal (resolved "a/../../../etc") (Some "/data/repos/octo/hello/etc") "however far it climbed first"

        // A session's own sandbox has no checkout for a relative path to be relative to, so
        // there is nothing to resolve against and nothing honest to invent.
        testCase "a workdir on a sandbox no repo declared is refused, naming the verb that does move one" <| fun () ->
            match SandboxDecl.toRequest None { SandboxDecl.empty with WorkingDirectory = Some "./app" } with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "./app") "it names the path"
                Expect.isTrue (e.Contains "set_shell_profile") "and what does move where terminals start"

        testCase "a declaration with no workdir needs no checkout" <| fun () ->
            Expect.equal
                (SandboxDecl.toRequest None { SandboxDecl.empty with Forward = [ "github" ] } |> expect)
                { SandboxRequest.defaults with Forward = [ "github" ] }
                "which is every ask the agent's own tool can make"

        // What crosses the command gate is a declaration, so the gate's args are bounded by
        // the FILE's schema rather than by a second one: whatever is written here, the file
        // must be willing to read back.
        testCase "a declaration written for the gate reads back as itself" <| fun () ->
            let declared =
                (ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": {
                        "dev": {
                          "container":
                            { "image": "node:24",
                              "cmd": "npm start",
                              "volumes": [ { "source": "workspace", "target": "/w", "mode": "ro" } ] },
                          "workdir": "app",
                          "env": { "NODE_ENV": "development", "DB": { "secret": "db-url" } },
                          "uses": [ "npm" ],
                          "uses": [ "npm" ],
                          "forward": [ "github" ] } } }"""
                 |> expect).Sandboxes
                |> Map.find (sandboxName "dev")
            Expect.equal
                (ConfigFile.parseSandbox (SandboxDecl.encode declared) |> expect)
                declared
                "every field survives the trip"

        // The point of the round trip being through the FILE's schema: a host path is
        // refused there, so it cannot cross the gate either — one rule, not two.
        testCase "a host-path volume cannot be written back through the gate" <| fun () ->
            let smuggled =
                { SandboxDecl.empty with
                    Container =
                        Some
                            { ContainerSpec.defaults with
                                Mounts = [ { Source = HostPath "/var/run/docker.sock"; Target = "/s"; Mode = ReadWrite } ] } }
            Expect.isError
                (ConfigFile.parseSandbox (SandboxDecl.encode smuggled))
                "the schema refuses on the way back in, wherever the declaration came from"

        // Saying nothing is not asking to be confined — what nothing means is the backend's
        // answer, and `forBackend` is where the two authors meet.
        testCase "a declaration with no container asks for no container, not for confinement" <| fun () ->
            Expect.equal
                (SandboxDecl.toRequest (Some "/checkout") SandboxDecl.empty |> expect)
                SandboxRequest.defaults
                "an empty declaration is the ask that names nothing in particular"

        testCase "a sandbox cannot ask for a process without a container to run it in" <| fun () ->
            // The point of the runtime union. `cmd` is not a sandbox key at all — it lives
            // inside `container`, so "a confined sandbox with a process" is not a state the
            // file can express and not one any decoder has to refuse.
            match ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "cmd": "npm start" } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "cmd") "it names the key that has no home here"
                Expect.isTrue (e.Contains "container") "and lists the one that does"

        testCase "a sandbox with no container block asks for no container" <| fun () ->
            // `None` is the repo saying nothing, not the repo asking to be confined — what
            // that means is the backend's answer, not this file's.
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "uses": [ "npm" ] } } }""" |> expect
            Expect.equal (file.Sandboxes |> Map.find (sandboxName "dev")).Container None "nothing was asked for"

        testCase "a secret is named, never carried" <| fun () ->
            // The whole reason `env` has two forms. A file that could hold a value would be
            // a file somebody commits a password into.
            let file =
                ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": { "dev": { "env": { "DATABASE_URL": { "secret": "db-url" } } } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            let expected = SecretName.create "db-url" |> expect
            Expect.equal (dev.EnvironmentVariables |> Map.tryFind "DATABASE_URL") (Some (SecretRef expected))
                "it decodes to a reference the type cannot put a value in"

        testCase "a file may not set anything under the reserved prefix" <| fun () ->
            // YESSION_LAUNCH is custody of the session's secrets; YESSION_BIN_* names a
            // binary this host executes. Refused as a prefix so the list never has to be
            // re-decided.
            let refused =
                ConfigFile.parse """
                    { "version": 2, "sandboxes": { "dev": { "env": { "YESSION_BIN_BWRAP": "/tmp/evil" } } } }"""
            Expect.isError refused "a repo cannot name the host's bubblewrap"

        testCase "the reserved refusal says which variable" <| fun () ->
            // A refusal nobody can act on gets worked around rather than fixed.
            match ConfigFile.parse """
                    { "version": 2, "sandboxes": { "dev": { "env": { "YESSION_LAUNCH": "x" } } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "YESSION_LAUNCH") "it names the variable it refused"

        // A file with one sandbox in it can only mean one thing by `$.sandboxes.env`. A file
        // with ten cannot, and the address was the same for all of them — so the refusal
        // named a key without naming which sandbox wrote it.
        testCase "a refusal is addressed to the sandbox that caused it" <| fun () ->
            match ConfigFile.parse """
                    { "version": 2,
                      "sandboxes": { "dev": { "workdir": "." },
                                     "gate": { "env": { "YESSION_LAUNCH": "x" } } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                // The refusal itself goes in the message: a bare `expected true` about a
                // string nobody can see is the failure that costs a second run to read.
                Expect.isTrue (e.Contains "sandboxes.gate") (sprintf "the path names the sandbox at fault, said: %s" e)
                Expect.isFalse (e.Contains "sandboxes.dev") (sprintf "and not the one that was fine, said: %s" e)

        testCase "an ordinary variable still passes" <| fun () ->
            // The guard above must not have made `env` useless.
            let file =
                ConfigFile.parse """
                    { "version": 2, "sandboxes": { "dev": { "env": { "NODE_ENV": "test" } } } }"""
                |> expect
            let dev = file.Sandboxes |> Map.find (sandboxName "dev")
            Expect.equal (dev.EnvironmentVariables |> Map.tryFind "NODE_ENV") (Some (PlainValue "test"))
                "a plain value is a plain value"

        testCase "an unknown key is refused, not skipped" <| fun () ->
            // A typo that decodes to "nothing was asked for" reads as configuration and
            // behaves as none.
            Expect.isError
                (ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "workdirr": "./app" } } }""")
                "a misspelled key fails the file"

        testCase "an unknown top-level key is refused too" <| fun () ->
            Expect.isError
                (ConfigFile.parse """{ "version": 2, "sandboxes": {}, "mcp": { "serial": "http://x" } }""")
                "a key this schema does not define is not silently ignored"

        testCase "a version this build does not speak is refused" <| fun () ->
            // A file from the future says so, rather than losing half its meaning to a
            // decoder that skips what it cannot read.
            Expect.isError (ConfigFile.parse """{ "version": 3, "sandboxes": {} }""")
                "version 3 is not decoded as a lossy version 2"

        testCase "a file with no version is refused" <| fun () ->
            Expect.isError (ConfigFile.parse """{ "sandboxes": {} }""") "the version is how the refusal above stays possible"

        testCase "a name a sandbox cannot have is refused where it is written" <| fun () ->
            // The name survives as a Docker object-name component and a directory.
            Expect.isError (ConfigFile.parse """{ "version": 2, "sandboxes": { "Dev Box": {} } }""")
                "an unusable name fails the file rather than a container start much later"

        testCase "two sandboxes with one name inside one file are refused" <| fun () ->
            // The ONLY place a clash can happen — across files the scope keeps them apart —
            // and it is refused where the person who wrote both can pick another name.
            // JSON's own duplicate-key handling would hide this, so the name is what repeats.
            let a = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": {}, "dev": {} } }"""
            match a with
            | Error _ -> ()
            | Ok file ->
                // A parser that folded the duplicate away must at least not invent two.
                Expect.equal (Map.count file.Sandboxes) 1 "a folded duplicate is one sandbox, never two"

        testCase "the union of two repos' files is total" <| fun () ->
            // The whole algebra. The keys are (repo, name) pairs and the repos are disjoint,
            // so both repos' `dev` survive and neither shadows the other.
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": {} } }""" |> expect
            let one, two = repo "octo/hello", repo "octo/other"
            let union = ConfigFile.union [ one, file; two, file ]
            Expect.equal (Map.count union) 2 "two repos asking for `dev` get two sandboxes"
            Expect.isTrue (union |> Map.containsKey (SandboxRef.inScope one (sandboxName "dev"))) "the first repo's"
            Expect.isTrue (union |> Map.containsKey (SandboxRef.inScope two (sandboxName "dev"))) "the second repo's"

        testCase "the union does not depend on the order the repos are folded" <| fun () ->
            // A union with an order dependence is a precedence rule nobody wrote down.
            //
            // The two files share a NAME and differ in content, which is what gives this
            // teeth: keyed on the name alone, the last fold would win and the answer would
            // depend on the order. Keyed by scope, there is nothing to win.
            let mine =
                ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "uses": [ "npm" ] } } }""" |> expect
            let theirs =
                ConfigFile.parse """{ "version": 2, "sandboxes": { "dev": { "uses": [ "npm" ] } } }""" |> expect
            let one, two = repo "octo/hello", repo "octo/other"
            Expect.equal
                (ConfigFile.union [ one, mine; two, theirs ])
                (ConfigFile.union [ two, theirs; one, mine ])
                "folding two files either way gives the same session"
            Expect.equal (Map.count (ConfigFile.union [ one, mine; two, theirs ])) 2
                "and neither repo's `dev` was lost to the other's"

        testCase "no file can declare the sandbox a terminal lands in by default" <| fun () ->
            // `default` is the session's, and a repo naming it gets its OWN, scoped.
            let file = ConfigFile.parse """{ "version": 2, "sandboxes": { "default": {} } }""" |> expect
            let union = ConfigFile.union [ repo "octo/hello", file ]
            Expect.isFalse (union |> Map.containsKey SandboxRef.defaultRef)
                "the session's own default is not something a checkout can take over"

        testCase "a sandbox is written as its scope and its name" <| fun () ->
            Expect.equal (SandboxRef.render SandboxRef.defaultRef) "default" "the session's own is just a name"
            Expect.equal
                (SandboxRef.render (SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")))
                "octo/hello:dev"
                "a repo's carries the repo"

        testCase "a written sandbox reads back as the same one" <| fun () ->
            // The agent addresses a sandbox by this string, so the round trip is a contract.
            let refs =
                [ SandboxRef.defaultRef
                  SandboxRef.inScope (repo "octo/hello") (sandboxName "dev") ]
            for r in refs do
                Expect.equal (SandboxRef.parse (SandboxRef.render r)) (Ok r) (sprintf "%s round-trips" (SandboxRef.render r))

        testCase "the session's own default keeps the bare session id it has always had" <| fun () ->
            // The whole behaviour-preservation claim of moving this derivation out of the
            // composition root. If this moves, every existing session's container, volume and
            // workspace moves with it.
            let session = SessionId.create "S0PZABC" |> expect
            Expect.equal (SandboxRef.objectName session SandboxRef.defaultRef) "S0PZABC"
                "the default sandbox's objects are named by the session, exactly as before"

        testCase "a session's named sandbox is the session id and the name" <| fun () ->
            let session = SessionId.create "S0PZABC" |> expect
            let review = SandboxRef.create SessionOwned (sandboxName "review")
            Expect.equal (SandboxRef.objectName session review) "S0PZABC-review" "unchanged too"

        testCase "two repos' same-named sandboxes name different objects" <| fun () ->
            // The reason this derivation exists at all. Two repos both declaring `dev` must
            // not end up sharing one container.
            let session = SessionId.create "S0PZABC" |> expect
            let mine = SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")
            let theirs = SandboxRef.inScope (repo "octo/other") (sandboxName "dev")
            Expect.notEqual (SandboxRef.objectName session mine) (SandboxRef.objectName session theirs)
                "one container each"

        testCase "a repo's sandbox does not collide with a session's of the same name" <| fun () ->
            let session = SessionId.create "S0PZABC" |> expect
            Expect.notEqual
                (SandboxRef.objectName session (SandboxRef.create SessionOwned (sandboxName "dev")))
                (SandboxRef.objectName session (SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")))
                "the scope is part of what names the object"

        testCase "the slug survives being a container name and a directory" <| fun () ->
            // `SandboxName`'s charset is narrow because these two consumers are; a slug that
            // carried the scope literally would put a '/' through both.
            let slug = SandboxRef.slug (SandboxRef.inScope (repo "octo/hello") (sandboxName "dev"))
            let safe c =
                (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-' || c = '_'
            Expect.isTrue (String.forall safe slug) (sprintf "'%s' is nameable" slug)

        testCase "the derived name is stable across calls" <| fun () ->
            // It names a container that outlives the process that made it, so a name that
            // varied would orphan the previous one on every boot.
            let session = SessionId.create "S0PZABC" |> expect
            let ref = SandboxRef.inScope (repo "octo/hello") (sandboxName "dev")
            Expect.equal (SandboxRef.objectName session ref) (SandboxRef.objectName session ref)
                "the same ref names the same object"

        testCase "a sandbox nobody could name is refused rather than parsed" <| fun () ->
            Expect.isError (SandboxRef.parse "octo/hello:dev:extra") "two colons name nothing"
            Expect.isError (SandboxRef.parse "not-a-repo:dev") "the scope has to be an owner/repo"
    ]

// -----------------------------------------------------------------------------
// What "the same sandbox" means (Plan 27, step 5b). The registry hands back a running
// sandbox when the ask matches and refuses when it does not, so this comparison decides
// between two bad outcomes: killing somebody's build, or handing back a sandbox that is
// not what was asked for. It is pure, so it is pinned here rather than through a registry.
// -----------------------------------------------------------------------------

/// Every way one ask can differ from the default, one per field the comparison looks at.
/// A list rather than separate cases because the property under test is about ALL of them
/// at once: whichever field moved, the refusal has something to say.
let private variants : (string * SandboxRequest) list =
    [ "forwarding", { SandboxRequest.defaults with Forward = [ "github" ] }
      "workdir",
        { SandboxRequest.defaults with
            Spec = { EnvironmentSpec.defaults with WorkingDirectory = Some "/somewhere" } }
      "environment",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    EnvironmentVariables = Map.ofList [ "NODE_ENV", PlainValue "development" ] } }
      "runtime", { SandboxRequest.defaults with Spec = EnvironmentSpec.container }
      "image",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime = Container { ContainerSpec.defaults with Image = Some { Name = "node"; Tag = Some "24" } } } }
      "command",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime = Container { ContainerSpec.defaults with Command = Some "postgres" } } }
      "mounts",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime =
                        Container
                            { ContainerSpec.defaults with
                                Mounts = [ { Source = SessionWorkspace; Target = "/work"; Mode = ReadWrite } ] } } }
      // The one no clause looks at, and the reason there is a backstop: two builds of the
      // same context differing only in which Dockerfile they use describe identically.
      "dockerfile",
        { SandboxRequest.defaults with
            Spec =
                { EnvironmentSpec.defaults with
                    Runtime =
                        Container
                            { ContainerSpec.defaults with
                                Build = Some { ContextPath = "."; DockerfilePath = Some "Dockerfile.ci" } } } } ]

let private sandboxRequestTests =
    testList "the same sandbox, or a different one (Plan 27)" [

        testCase "an ask that matches has nothing to say about itself" <| fun () ->
            for name, variant in ("default", SandboxRequest.defaults) :: variants do
                Expect.equal
                    (SandboxRequest.differences variant variant)
                    []
                    (sprintf "'%s' against itself is the idempotent case" name)

        // THE property, and the reason `differences` is a function rather than a sentence
        // assembled at the refusal: whenever the registry declines to hand a sandbox back,
        // it must be able to say why. A clause list that could come back empty would make
        // the refusal claim a difference and then name none of it.
        testCase "whatever moved, the refusal has a clause for it" <| fun () ->
            for name, variant in variants do
                Expect.isNonEmpty
                    (SandboxRequest.differences SandboxRequest.defaults variant)
                    (sprintf "a '%s' difference is describable" name)
                Expect.isNonEmpty
                    (SandboxRequest.differences variant SandboxRequest.defaults)
                    (sprintf "and so is a '%s' difference the other way round" name)

        // The backstop, on the case that reaches it: two asks that describe the same way and
        // still are not the same ask. It says less, and that is the point — a refusal that
        // said nothing here would let the registry treat them as one sandbox.
        testCase "a difference no clause looks at is still a difference" <| fun () ->
            let buildOf dockerfile =
                { SandboxRequest.defaults with
                    Spec =
                        { EnvironmentSpec.defaults with
                            Runtime =
                                Container
                                    { ContainerSpec.defaults with
                                        Build = Some { ContextPath = "."; DockerfilePath = Some dockerfile } } } }
            Expect.isNonEmpty
                (SandboxRequest.differences (buildOf "Dockerfile") (buildOf "Dockerfile.ci"))
                "two builds of one context are not interchangeable"

        // Names, never values. The registry's refusal reaches the model and the timeline,
        // and an environment variable's value is the one thing neither may carry.
        testCase "a differing environment is described by name, never by value" <| fun () ->
            let secret =
                { SandboxRequest.defaults with
                    Spec =
                        { EnvironmentSpec.defaults with
                            EnvironmentVariables = Map.ofList [ "DATABASE_URL", PlainValue "postgres://hunter2" ] } }
            let said = SandboxRequest.differences SandboxRequest.defaults secret |> String.concat "; "
            Expect.isTrue (said.Contains "DATABASE_URL") "it names the variable"
            Expect.isFalse (said.Contains "hunter2") "and not what is in it"
    ]

let private deliveryFilterTests =
    let path raw = FieldPath.create raw |> expect
    /// A delivery as the relay presents one: whatever the caller asks for, by path.
    let delivery (fields: (string * string) list) =
        fun p -> fields |> List.tryFind (fun (k, _) -> k = FieldPath.render p) |> Option.map snd

    testList "Delivery filters (the hook relay)" [
        testCase "a path is dotted segments, lowercased so a header's case cannot matter" <| fun () ->
            Expect.equal
                (FieldPath.render (path "Body.Repository.Full_Name"))
                "body.repository.full_name"
                "the rendering is what travels, and it is one canonical form"

        testCase "a path with an empty segment is not a path" <| fun () ->
            Expect.isError (FieldPath.create "body..name") "an empty segment addresses nothing"

        testCase "a blank path is not a path" <| fun () ->
            Expect.isError (FieldPath.create "   ") "there is nothing to address"

        testCase "every constraint must hold" <| fun () ->
            let filter =
                { Where =
                    [ path "body.repository.full_name", "trinketworks/yession"
                      path "headers.x-github-event", "pull_request" ] }
            let matching =
                delivery
                    [ "body.repository.full_name", "trinketworks/yession"
                      "headers.x-github-event", "pull_request" ]
            Expect.isTrue (DeliveryFilter.matches filter matching) "both hold, so it matches"

        testCase "one constraint that does not hold is enough to refuse" <| fun () ->
            let filter =
                { Where =
                    [ path "body.repository.full_name", "trinketworks/yession"
                      path "headers.x-github-event", "pull_request" ] }
            let other =
                delivery
                    [ "body.repository.full_name", "trinketworks/yession"
                      "headers.x-github-event", "issues" ]
            Expect.isFalse (DeliveryFilter.matches filter other) "a conjunction is refused by any one of its parts"

        testCase "a path the delivery does not carry fails its constraint" <| fun () ->
            // The direction that matters: an absent field must never match by accident,
            // because that is how one session would start receiving another's deliveries.
            let filter = { Where = [ path "body.repository.full_name", "trinketworks/yession" ] }
            Expect.isFalse (DeliveryFilter.matches filter (delivery [])) "absent is not equal"

        testCase "a filter with no constraints accepts everything on its endpoint" <| fun () ->
            Expect.isTrue
                (DeliveryFilter.matches DeliveryFilter.everything (delivery []))
                "no constraints is not a special case, it is an empty conjunction"
    ]

let tests =
    testList "Domain" [
        identityTests
        launchTests
        configTests
        sandboxRequestTests
        modelTests
        authorityTests
        envelopeSerializationTests
        conversationProjectionTests
        repoTests
        prWatchTests
        deliveryFilterTests
        shellProfileTests
        frameSerializationTests
    ]
