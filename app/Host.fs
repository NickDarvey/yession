module Yession.Host.Host

// The Session Process host: owns the event log and accepts WebRTC peer connections,
// running the token-gated peer-session handshake for each. This is the composition root
// for the running process (design.md §1 "Composition at the top", §2.1).

open System
open Yjs
open Yession.Domain
open Yession.Domain.Terminals
open Yession.Domain.Collab
open Yession.Domain.Tools
open Yession.SessionProcess

[<Fable.Core.Emit("setInterval($1, $0)")>]
let private setInterval (ms: int) (callback: unit -> unit) : obj = Fable.Core.Util.jsNative

[<Fable.Core.Emit("clearInterval($0)")>]
let private clearInterval (handle: obj) : unit = Fable.Core.Util.jsNative

/// How often a busy session repeats its activity report (Plan 11). Comfortably shorter
/// than any sane idle window, so a Manager reaping on silence needs several missed beats
/// in a row before it acts — one dropped request never costs a session.
let private activityBeatMs = 30000

/// How often the idle-lease sweep runs (Plan 13, stage 3c). Its own beat rather than the
/// activity one, because that beat exists only for a session with a Manager to report to —
/// and a terminal held open in a Manager-less session starves its queue exactly the same.
/// Well under `TerminalLeaseIdle.window`, so the overshoot is bounded by the beat.
let private idleLeaseBeatMs = 30000

type SessionHost =
    { SessionId : SessionId
      /// Mint an unattributed peer token valid for this process — what tests/headless
      /// clients use to join (`PeerHello.Token`) and read `/events`.
      MintPeerToken : unit -> string
      /// Mint a peer token carrying an explicit attribution — what `/me` does for a
      /// browser whose cookie names a Manager-verified user.
      MintPeerTokenAs : PeerAttribution -> string
      Port : int
      Log : EventLog<SessionEvent>
      /// The session's Yjs document. The Session Process owns it; peers hold replicas
      /// synced over `State` frames.
      Doc : Y.Doc
      /// The session's `default` WorkSandbox (Step 12), the one every session has had.
      Environment : SessionEnvironment.SessionEnvironment
      /// Every WorkSandbox the session has, by name (Plan 15, stage 2). The commands that
      /// start and stop them are the agent's; this is where they land.
      Sandboxes : WorkSandboxes.WorkSandboxes
      /// The session's terminals (Plan 13) — opening one starts the environment.
      Terminals : SessionTerminals.SessionTerminals
      /// Tell the command gate how each gated command is actually carried out (Plan 15,
      /// stage 3b). Set once, from SessionMain, where the repo and sandbox services are
      /// composed — the Host owns the gate because it owns the doc and the log, and the
      /// table has to arrive from the layer above it.
      SetCommandDispatch : CommandDispatch -> unit
      /// The gate itself, for the one caller that is not a turn: the fold over every
      /// checkout's `yession.yaml` (Plan 27). It goes through the SAME door the agent's
      /// calls do — which is the reason the gate was made a capability rather than a detail
      /// of the MCP adapter, and the reason there is no second path to a sandbox.
      RunGated : RunGatedCommand
      /// The agent's terminal capabilities (Plan 13, stage 3b), exposed so a test can drive
      /// the agent's HALF of the approval flow without a model in the loop. Production reaches
      /// them through `AgentCapabilities`, which is built from this same value.
      TerminalCommands : TerminalCommands.TerminalCommands
      /// Resolves when the next peer session ends. Register (call) it *before* triggering
      /// the disconnect you want to observe, then await it — this avoids any reliance on
      /// timing to see the resulting `PeerLeft`.
      WaitForNextSessionEnd : unit -> Async<unit>
      /// Run a peer session over an arbitrary channel — the exact per-peer pump a WebRTC
      /// connection drives. Production wires this to the WebRTC listener (`Signalling`);
      /// tests connect an `InMemoryChannel` pair here to drive the real Host (relay, drain,
      /// scheduler, presence, title report) with no WebRTC, HTTP, or native addon.
      Connect : FrameChannel<string> -> unit
      Stop : unit -> Async<unit> }

/// Start a Session Process: create the event log and the session's Yjs document, start
/// HTTP bootstrap + signalling, and run a peer session for every connection. Each
/// accepted peer receives the full doc state, then incremental updates are relayed
/// between peers through the doc. The Process is the single consumer of the shared
/// message queue: when the agent is idle it drains the queue into `MessageSent` events
/// and — when `runAgent` is given — starts one coalesced turn per batch (Phase 3).
/// When `makeSandboxes` is given (the session's WorkSandbox registry over the sandbox
/// seam), the session gets named, lazily-started environments (Step 12, named in Plan 15)
/// wired to the log this host creates. When `docStore` is given (Step 19), the persisted doc is
/// replayed at boot — unsent queue entries and drafts survive restarts — and every
/// subsequent update is durably appended. Resolves once the server is listening.
let startFull
    // A THUNK, read at every drain (Plan 08): agent availability is dynamic — a
    // credential connected mid-session enables turns without a relaunch.
    (runAgent: unit -> RunAgent option)
    (makeSandboxes: (EventLog<SessionEvent> -> WorkSandboxes.WorkSandboxes) option)
    // Secrets (Plan 06): the Manager-granted, session-scoped secrets surface
    // (write/list/delete — never read). None = turns see the `none` denials.
    (secretsCapabilities: ControlClient.SessionSecretsCapabilities option)
    (baseLog: EventLog<SessionEvent> option)
    (docStore: DocStore.DocStore option)
    // Terminal transcripts (Plan 13). `None` = an in-memory store, so terminals behave
    // identically in a test host and the only thing missing is what outlives the process.
    (transcriptStore: TranscriptStore.TranscriptStore option)
    (reportName: (string -> Async<unit>) option)
    // Plan 11: report whether this session is in use, so the Manager can stop it when it
    // is not. Absent for a session with no Manager — nothing would be listening.
    (reportActivity: (bool -> Async<unit>) option)
    // Telemetry sink (Plan 04): the completed-turn usage emitter, threaded to the
    // scheduler. `ignore` in the layered helpers below and whenever telemetry is off.
    (emitUsage: AgentTurnId -> AgentUsage -> unit)
    (subscribeNotifications: Subscribe<SessionNotification> option)
    // The MCP servers this session was given (Plan 17). Composed by the caller rather than
    // subscribed here, unlike the other reverse legs: what arrives on that leg has to
    // invalidate a QUERY as well as rebuild a registry, and the query surface is the
    // composition root's. `McpConnections.none` is a session that was given none.
    (mcpConnections: McpClient.McpConnections)
    // Extra HTTP routes on the session's server (Plan 08: the connection surface).
    (extraHttpRoutes: (Interop.IncomingMessage -> Interop.ServerResponse -> bool) option)
    (sessionId: SessionId)
    (auth: SessionAuth.Auth option)
    // The path this session is served under (`""` at an origin root).
    (mount: string)
    // The Manager's public origin, baked into the shell so a disconnected client knows
    // where to ask for this session back (Plan 11). None for a Manager-less session:
    // there is then nothing to ask.
    (managerOrigin: string option)
    // Whether a session keeps its address across launches (Plan 12); false means the
    // client's local-first promise has to be qualified. Baked into the shell.
    (ephemeralStorage: bool)
    (port: int)
    : Async<SessionHost> =
    async {
        let doc = Y.Doc.Create ()
        // Peer access: every joining peer presents a token THIS process minted (served
        // to authenticated browsers by `/me`, handed to tests via `MintPeerToken`).
        let peerTokens = PeerTokens (Interop.randomSecret)
        // Restart ordering (Step 19): replay the persisted doc FIRST — before any
        // observer registers — then open the log, then the boot drain repairs the
        // crash-between-append-and-removal window via the log-anchored dedup.
        docStore |> Option.iter (fun store -> store.ReplayInto doc)
        // The persistence tap registers before every other observer, so an update is
        // durable before anything acts on it.
        docStore |> Option.iter (fun store -> DocSync.onAnyUpdatePayload doc store.Append)

        // Legacy draft garbage: builds before the slot-follows-body rule (`DraftSlot`) published a
        // draft slot the moment a client mounted its composer, so a replayed doc carries an empty
        // slot for every peer that ever opened the session — each one an empty draft box on
        // everyone's composer. Drop them here: no peer is connected yet, so an empty body cannot be
        // a draft in progress. The removal rides the tap above (durable), and every replica merges
        // the delete on its next state exchange.
        match SyncedStateSync.removeEmptyDrafts doc with
        | [] -> ()
        | dropped ->
            eprintfn "[session %s] dropped %d empty draft slot(s) at boot"
                (SessionId.value sessionId) (List.length dropped)


        // Connected peers' channels, for state relay; keyed per connection.
        let mutable connections : Map<int, FrameChannel<string>> = Map.empty
        let mutable nextConnectionId = 0

        let sendState (channel: FrameChannel<string>) (payload: string) =
            Async.StartImmediate (channel.Send (State (StateSync payload)))

        let broadcastExcept (except: int) (payload: string) =
            connections |> Map.iter (fun id channel -> if id <> except then sendState channel payload)

        // Ephemeral cursor presence: relayed to every OTHER peer, never applied to the doc
        // or the log. A peer disconnecting relays a cleared cursor so its caret vanishes.
        let broadcastPresenceExcept (except: int) (payload: PresencePayload) =
            connections
            |> Map.iter (fun id channel -> if id <> except then Async.StartImmediate (channel.Send (Presence payload)))

        // Every durable fact is advertised: appends go through a log wrapper that
        // broadcasts the new latest offset to all connected peers (clients page the
        // actual events in Step 07).
        let broadcastEventsAvailable (offset: EventOffset) =
            connections
            |> Map.iter (fun _ channel ->
                Async.StartImmediate (channel.Send (EventLog (EventsAvailable offset))))

        // Peer→user attribution, derived from the durable log so it is
        // restart-safe by construction: every `PeerJoined` carrying a Manager-verified
        // user binds that peer, live joins update it through the append wrapper below,
        // and a departed peer keeps its last-known binding — a still-queued message from
        // a peer that left drains attributed.
        let mutable peerUsers : Map<string, UserId> = Map.empty
        let recordAttribution (event: SessionEvent) : unit =
            match event with
            | PeerJoined { PeerId = peer; User = Some user } ->
                peerUsers <- Map.add (PeerId.value peer) user peerUsers
            | _ -> ()
        let actorFor (peerId: PeerId) : ActorRef =
            match Map.tryFind (PeerId.value peerId) peerUsers with
            | Some user -> UserRef user
            | None -> PeerRef peerId

        // The terminal projection as the Process itself has it, folded forward on every
        // append. The Process is the log's only writer, so this is complete and ordered by
        // construction — and it is what `execute_command`'s wait observes, which is why the
        // wait holds no state of its own.
        let mutable terminalProjection = TerminalProjection.empty
        // Anything that could change a waiting command's outcome: an appended event, a doc
        // update. One signal for both, because a waiter does not care which happened — it
        // re-reads and decides.
        let mutable changeListeners : Map<int, unit -> unit> = Map.empty
        let mutable nextChangeListener = 0
        let subscribeToChanges (listener: unit -> unit) : unit -> unit =
            let id = nextChangeListener
            nextChangeListener <- nextChangeListener + 1
            changeListeners <- Map.add id listener changeListeners
            fun () -> changeListeners <- Map.remove id changeListeners
        let notifyChanged () = changeListeners |> Map.iter (fun _ listener -> listener ())

        let log =
            // Durable storage is injected (file-backed in the product); the in-memory
            // log remains the deterministic default.
            let inner =
                match baseLog with
                | Some injected -> injected
                | None -> InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
            { inner with
                Append =
                    fun actor event ->
                        async {
                            recordAttribution event
                            let! appended = inner.Append actor event
                            terminalProjection <- TerminalProjection.applyEvent terminalProjection event
                            broadcastEventsAvailable appended.Offset
                            notifyChanged ()
                            return appended
                        } }

        // Seed the scheduler's log-anchored dedup set from the durable log (the
        // restart case): exactly-once is anchored in the log, not the doc.
        let! replayed = log.Read None Int32.MaxValue
        replayed.Events |> List.iter (fun e -> recordAttribution e.Event)
        let initialConsumed =
            replayed.Events |> List.choose (fun e -> QueueDrain.consumedOf e.Event) |> Set.ofList
        // The terminal drain's own exactly-once anchor, and the terminals a previous
        // process left open — both folded from the same durable replay.
        let initialTerminalConsumed =
            replayed.Events |> List.choose (fun e -> TerminalQueueDrain.consumedOf e.Event) |> Set.ofList
        let replayedTerminals =
            replayed.Events
            |> List.fold (fun proj e -> TerminalProjection.applyEvent proj e.Event) TerminalProjection.empty
        terminalProjection <- replayedTerminals
        // Where a shell opened in each sandbox starts (Plan 25), from the same replay — which
        // is what makes a restarted session open its next terminal where the last one started.
        let replayedProfiles =
            replayed.Events
            |> List.fold (fun proj e -> ShellProfileProjection.applyEvent proj e.Event) ShellProfileProjection.empty

        // The session's WorkSandboxes (Plan 15, stage 2): a registry keyed by name, each
        // entry lazily created on first need. `default` is the one every session has had,
        // so a Host composed without sandboxes still answers every question — its default
        // environment records needs as unavailable, exactly as before.
        let sandboxes =
            match makeSandboxes with
            | Some make -> make log
            | None -> WorkSandboxes.unavailable
        let environment = sandboxes.EnvironmentFor SandboxRef.defaultRef

        let mintTurnId () =
            match AgentTurnId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "agent turn id invariant violated: %s" e
        let mintMessageId () =
            match MessageId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "message id invariant violated: %s" e
        let mintToolUseId () =
            match ToolUseId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "tool use id invariant violated: %s" e
        // --- Terminals (Plan 13) -----------------------------------------------------
        //
        // A second consumer over a second queue, sharing nothing with the agent scheduler
        // but the doc updates that wake both. That independence is the design: a build
        // running in a terminal must not stop the agent answering, and a long agent turn
        // must not stop someone running `git status`.
        let transcripts = transcriptStore |> Option.defaultWith TranscriptStore.inMemory
        // Assigned once the terminal scheduler exists, below. The terminal manager is built
        // first (the scheduler consumes it), and it needs to wake the drain when a lease ends.
        let mutable reDrainTerminals : unit -> unit = ignore
        let mintBlockId () =
            match BlockId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "block id invariant violated: %s" e

        // A record is broadcast only AFTER the transcript has it (the store appends before
        // returning the seq), so a dropped frame costs latency and never the record. A peer
        // that misses one re-reads it over HTTP; nothing here is the only copy.
        let broadcastTerminalRecord (terminal: TerminalId) (seq: int) (record: TranscriptRecord) =
            connections
            |> Map.iter (fun _ channel ->
                Async.StartImmediate (channel.Send (Terminal (TerminalRecord (terminal, seq, record)))))

        // A terminal now EXISTS, which a peer that is already connected cannot learn any
        // other way: it was not here to be told at accept, and `Screens.Sync` folds records
        // only into an emulator a snapshot created — so without this every record of a
        // terminal opened mid-session is dropped on arrival.
        //
        // Seq 0, an empty screen, and the size every terminal opens at, because that is the
        // whole truth at open: there is nothing yet to catch up on, nothing has resized it,
        // and a client cannot invent the origin for itself.
        let broadcastTerminalOpened (terminal: TerminalId) =
            connections
            |> Map.iter (fun _ channel ->
                Async.StartImmediate (
                    channel.Send (
                        Terminal (
                            TerminalSnapshot (
                                terminal,
                                { Seq = 0
                                  Cols = TerminalSize.default'.Cols
                                  Rows = TerminalSize.default'.Rows
                                  Screen = "" })))))

        let terminals =
            SessionTerminals.create
                log
                sandboxes.EnvironmentFor
                transcripts.Open
                transcripts.ReadRange
                Emulator.openEmulator
                SessionTerminals.TerminalShell.posix
                (fun () -> DateTimeOffset.UtcNow)
                TerminalId.mint
                mintBlockId
                // Cryptographically random, and not a counter: a guessable nonce is no nonce
                // at all, since the whole point is that output nobody trusted cannot forge a
                // block's outcome.
                Interop.randomSecret
                mintMessageId
                broadcastTerminalRecord
                broadcastTerminalOpened
                // Re-arm the terminal drain when a lease ends (Plan 13, stage 2e). The
                // scheduler is built from `terminals`, so this is the one indirection the
                // cycle needs: a lease that ends inside the manager — the alt-screen flip, a
                // dropped peer — has nothing the scheduler could have observed.
                (fun () -> reDrainTerminals ())
                // How a foreign stream is reached (Plan 16, part D). One implementation, not
                // a configurable one: the wire is part of the seam's contract, so a second
                // way to attach would be a second contract nobody wrote down.
                AttachWs.attach
                // The gate on every block (Plan 23). The bypass until an AI-driven
                // classifier exists; this root supplies it and computes nothing.
                Classifier.approveAll
                (replayedTerminals |> TerminalProjection.openTerminals |> List.map (fun t -> t.TerminalId))
                replayedProfiles

        // The agent's ONE execution path (Plan 13, stage 3b). It queues a command where
        // people can see it and then WAITS — bounded by the command timeout — so the agent
        // gets its answer back without a turn ever hanging.
        let terminalCommands =
            TerminalCommands.create
                doc
                terminals
                (fun () -> terminalProjection)
                (fun () -> SyncedStateSync.ofDoc doc)
                (fun id fromSeq toSeq -> transcripts.ReadRange id fromSeq toSeq |> Transcript.printed)
                terminals.AgentTerminal
                (fun () ->
                    match QueueId.create (string (Guid.NewGuid ())) with
                    | Ok id -> id
                    | Error e -> failwithf "queue id invariant violated: %s" e)
                (fun () -> DateTimeOffset.UtcNow)
                subscribeToChanges

        // How each gated command is actually carried out, by tool name. Assigned once
        // SessionMain has composed the services; empty until then, which is honest — a
        // command proposed before the session is up has nothing to run it.
        let commandDispatch : CommandDispatch ref = ref Map.empty

        // The gate for structured commands (Plan 15, stage 3b; Plan 23): every command
        // passes the classifier on its way to the dispatch table, and hands back the same
        // `QueueId` a terminal command does — which is what lets ONE `check_pending` serve
        // both. The classifier is the bypass until an AI-driven one exists (GAPS.md).
        let commandGate =
            CommandGates.create
                Classifier.approveAll
                // A getter: the table is assembled in SessionMain, where the repo and
                // sandbox services are, which is after this Host exists.
                (fun () -> commandDispatch.Value)
                (fun actor event ->
                    async {
                        let! _ = log.Append actor event
                        return ()
                    })
                (fun () ->
                    match QueueId.create (string (Guid.NewGuid ())) with
                    | Ok id -> id
                    | Error e -> failwithf "queue id invariant violated: %s" e)
                mintMessageId
                (fun () -> DateTimeOffset.UtcNow)
                subscribeToChanges

        // A handle names a REQUEST without saying which kind it is, so the join lives here
        // and the agent learns one verb. The gate is asked first because it answers from an
        // in-memory table and the terminal side walks the projection.
        let checkPending (handle: QueueId) : Async<Result<PendingOutcome, string>> =
            async {
                if commandGate.Knows handle then
                    match! commandGate.Read handle with
                    | Ok outcome -> return Ok (PendingCommand outcome)
                    | Error reason -> return Error reason
                else
                    match! terminalCommands.Read handle with
                    | Ok outcome -> return Ok (PendingTerminal outcome)
                    | Error reason -> return Error reason
            }
        /// The audit seam (Plan 16, part C), bound to one turn. Every tool call the turn
        /// makes appends here — the session's own verbs today, a proxied provider's
        /// tomorrow — and `set_secret` is the reason it exists: writing a secret was
        /// recorded NOWHERE, so a human could not find out that the agent had done it.
        ///
        /// The id is minted here rather than derived, for the same reason `BlockId` is: a
        /// chip somebody will tap, and eventually link to, must not be identified by a rule
        /// that lives nowhere in the data.
        let toolUseLogFor (turnId: AgentTurnId) : ToolUseLog =
            { Started =
                fun (started: ToolUseBegin) ->
                    async {
                        let id = mintToolUseId ()
                        let! _ =
                            log.Append
                                ActorRef.Agent
                                (SessionEvent.ToolUseStarted
                                    { ToolUseId = id
                                      AgentTurnId = turnId
                                      Namespace = started.Namespace
                                      Name = started.Name
                                      Arguments = started.Arguments })
                        return Some id
                    }
              Finished =
                fun id (ended: ToolUseEnd) ->
                    async {
                        let! _ =
                            log.Append
                                ActorRef.Agent
                                (SessionEvent.ToolUseFinished
                                    { ToolUseId = id; Outcome = ended.Outcome; Block = ended.Block })
                        return ()
                    } }

        /// A stream a provider offers becomes a terminal (Plan 19). Session-scoped, because
        /// a provider polled across two turns is offering one stream and not two; the
        /// per-turn ceiling lives inside `Decorate`, which the turn calls.
        ///
        /// Opened as the AGENT, which is the truth: the call that produced the offer was the
        /// agent's, and the terminal it became is attributed to whoever caused it — the same
        /// rule an agent's block already follows.
        let streamAttacher =
            ToolStreams.create
                // The registries as they stand NOW, for a reattach: a person pressing a
                // button an hour later should reach whatever servers this session has, not
                // the snapshot some turn was given.
                (fun () -> mcpConnections.Registries ())
                { Open = fun offer -> terminals.Open ActorRef.Agent (Attached offer) offer.Ticket.Label
                  IsOpen = terminals.IsOpen }

        let capabilitiesFor (turnId: AgentTurnId) (turnActor: ActorRef) : AgentCapabilities =
            { // Bound to THIS turn's actor (Plan 20), which is what a queued command records
              // as the authority it borrows and what a WOKEN turn later resolves its own from.
              // The agent cannot name it: an acting party that could choose whose credential
              // it runs on is not gated by one.
              ExecuteCommand = fun request -> terminalCommands.Execute request (Authority.agentFor turnActor)
              CheckPending = checkPending
              // The terminal verbs a person already has (Plan 20, stage 3), reaching the same
              // manager the list's buttons do. One implementation, two surfaces.
              OpenTerminal =
                fun name sandbox ->
                    terminals.OpenAgentTerminal (defaultArg sandbox SandboxRef.defaultRef) name
              CloseTerminal =
                fun id ->
                    async {
                        // A person typing in their own shell is not the agent's to end. The
                        // list gives THEM the same verb over the agent's terminals, which is
                        // the asymmetry worth having: a human can always stop the machine.
                        if not (terminals.OpenedByAgent id) then
                            return Error "that terminal is not yours — it belongs to someone in this session"
                        else return! terminals.Close id "the agent finished with it"
                    }
              ListTerminals =
                fun () ->
                    async {
                        let busy = terminals.Busy ()
                        return
                            Ok
                                (TerminalProjection.openTerminals terminalProjection
                                 |> List.map (fun view ->
                                     { Terminal = view.TerminalId
                                       Name = view.Title
                                       Sandbox = view.Sandbox
                                       Mine = terminals.OpenedByAgent view.TerminalId
                                       Busy = Set.contains (TerminalId.value view.TerminalId) busy }))
                    }
              // The agent's hand in a terminal that has no blocks (Plan 19). It takes the
              // lease like a peer, so a human watching sees who is typing and can take it
              // back — which is the whole reason this is a terminal verb rather than a
              // provider's own write tool.
              WriteTerminal =
                fun id data ->
                    async {
                        match! terminals.Write id ActorRef.Agent data with
                        | Ok () -> return Ok (sprintf "typed into terminal %s; you now hold it" (TerminalId.value id))
                        | Error reason -> return Error reason
                    }
              // `write_terminal`'s other half, and it lives beside it in the terminal
              // manager: the rule that admits one admits the other, and the composition root
              // is not where a bound belongs.
              ReadTerminal = terminals.Tail
              RunGated = commandGate.Run
              SetSecret =
                match secretsCapabilities with
                | Some secrets -> secrets.SetSecret
                | None -> AgentCapabilities.none.SetSecret
              ListSecrets =
                match secretsCapabilities with
                | Some secrets -> secrets.ListSecrets
                | None -> AgentCapabilities.none.ListSecrets
              DeleteSecret =
                match secretsCapabilities with
                | Some secrets -> secrets.DeleteSecret
                | None -> AgentCapabilities.none.DeleteSecret
              // The repo verbs (Plan 14) are denials HERE and rebound per turn by
              // SessionMain's dispatching wrapper — the token is the TURN ACTOR's, and
              // only the dispatcher knows who that is.
              AddRepo = AgentCapabilities.none.AddRepo
              RemoveRepo = AgentCapabilities.none.RemoveRepo
              SwitchRepoBranch = AgentCapabilities.none.SwitchRepoBranch
              FetchRepo = AgentCapabilities.none.FetchRepo
              RepoStatus = AgentCapabilities.none.RepoStatus
              RepoLog = AgentCapabilities.none.RepoLog
              RepoDiff = AgentCapabilities.none.RepoDiff
              // Likewise the query surface (Plan 15): the registry is composed in
              // SessionMain, so a Host started without it declares nothing rather than
              // declaring queries it cannot answer.
              Queries = AgentCapabilities.none.Queries
              ReadQuery = AgentCapabilities.none.ReadQuery
              // The sandbox commands (Plan 15, stage 2) are denials HERE and rebound per
              // turn by SessionMain, for the repo verbs' reason: the credential a
              // forwarding start resolves belongs to the TURN ACTOR, and only the
              // dispatcher knows who that is.
              StartWorkSandbox = AgentCapabilities.none.StartWorkSandbox
              StopWorkSandbox = AgentCapabilities.none.StopWorkSandbox
              SetShellProfile = AgentCapabilities.none.SetShellProfile
              RecordToolUse = toolUseLogFor turnId
              // Snapshotted HERE, which is what makes a turn's tool list stable: a set
              // change mid-turn lands on the next turn, never underneath the model. Each is
              // decorated so a stream a provider offers becomes a terminal (Plan 19) — over
              // the REGISTRY rather than inside the client, so the seam sits where every
              // call already passes, beside the audit's.
              ForeignTools = mcpConnections.Registries () |> List.map streamAttacher.Decorate }

        // The queue drain and turn scheduler (Phase 3) — the real machinery lives in
        // `Scheduler` (shared with the property harness); the Host wires it to this
        // session's doc, log, environment capabilities, and command surface.
        let scheduler =
            Scheduler.create sessionId doc log runAgent capabilitiesFor emitUsage mintTurnId mintMessageId actorFor transcripts.ReadRange initialConsumed
        let drain () = scheduler.Drain ()
        let requestInterrupt = scheduler.RequestInterrupt

        // A terminal block finishing is the one thing outside the agent's own turn that can
        // make the log owe it a turn nobody asked for (Plan 20, stage 2). The terminal
        // scheduler is what knows one finished; the wake is what that is worth here.
        let terminalScheduler = TerminalScheduler.create doc terminals scheduler.Wake initialTerminalConsumed
        let drainTerminals () = terminalScheduler.Drain ()
        reDrainTerminals <- drainTerminals
        // The bound on the starvation a live holder can cause (Plan 13, stage 3c). Reclaims
        // only a lease that has gone idle WITH something queued behind it, so a peer nobody is
        // waiting on keeps their terminal for as long as they like.
        // Kept so `Stop` can clear it. Unlike the activity beat this one runs in EVERY
        // session, so a timer left armed past the end of a session is a timer reading a doc
        // and a log that nothing owns any more.
        let idleLeaseBeat = setInterval idleLeaseBeatMs terminalScheduler.ReclaimIdleLeases

        // Process-originated doc writes (the drain's queue removals) broadcast to every
        // peer; peer payloads are relayed by the receiving connection.
        // Not released: this one belongs to the SESSION, and it outlives every peer in the
        // map it broadcasts to (a client's own per-channel listener is the one that goes,
        // in `App.connect`).
        DocSync.onLocalUpdate doc (fun payload ->
            connections |> Map.iter (fun _ channel -> sendState channel payload))
        |> ignore

        // The drain re-arms on every doc update observed while idle, so an enqueue can
        // never be missed (liveness; recursion during a drain's own removal is cut by the
        // single-flight guard). Drafts are ephemeral WIP in the synced state — never
        // durable facts (only their send is) — so a new draft appearing needs no append.
        DocSync.onAnyUpdate doc (fun () -> drain ())
        // The terminal queue re-arms on the same signal, for the same liveness reason.
        DocSync.onAnyUpdate doc (fun () -> drainTerminals ())
        // ...and so does a command waiting on that queue: an approval a peer just granted is a
        // doc write, and nothing else would tell the wait about it.
        DocSync.onAnyUpdate doc notifyChanged

        // --- Is this session in use? (Plan 11) ---------------------------------------
        //
        // Three conditions, and the second is the one an outside observer cannot get
        // right: a turn running with nobody watching is a session very much in use, and
        // it can go minutes without appending anything (a long tool call), so any
        // file-mtime proxy would reap it mid-work. The first covers a human reading with
        // the tab open — also silent, also in use. The third closes the window between a
        // message being accepted and the turn for it starting.
        //
        // Drafts and presence are deliberately NOT inputs: they belong to a connected
        // peer, so they are already covered by the first condition.
        let isBusy () =
            not (Map.isEmpty connections)
            || Option.isSome (scheduler.RunningTurn ())
            // A command running in a terminal is a session in use even with nobody
            // watching — a long build is exactly the case an outside observer would get
            // wrong, and reaping the session would kill the build.
            || not (Set.isEmpty (terminals.Busy ()))
            || (match SyncedStateSync.ofDoc doc with
                | Ok synced -> not (Map.isEmpty synced.Queue) || not (Map.isEmpty synced.Pending)
                | // A doc that will not decode is a session in trouble, and stopping it
                  // out from under its owner is the wrong response to that. Hold it busy
                  // and let something that understands the failure deal with it.
                  Error _ -> true)

        // Transitions go out at once, so idleness starts counting the moment the last peer
        // leaves and a returning one cancels the countdown without waiting for a tick. The
        // repeat is what makes the mechanism robust rather than merely prompt: the Manager
        // reaps on SILENCE, so no single delivery has to succeed. Idle is reported once per
        // transition and then left alone; busy repeats, because busy is the claim that
        // needs renewing.
        let notifyActivity : unit -> unit =
            match reportActivity with
            | None -> ignore
            | Some report ->
                let mutable lastReported : bool option = None
                fun () ->
                    let busy = isBusy ()
                    if busy || lastReported <> Some busy then
                        lastReported <- Some busy
                        Async.StartImmediate (report busy)

        if Option.isSome reportActivity then
            notifyActivity ()
            setInterval activityBeatMs notifyActivity |> ignore
            DocSync.onAnyUpdate doc notifyActivity

        // Report the collaborative title to the Manager (metadata, not an event) so the
        // session list reflects it. Last-value debounced: only a changed, non-empty title
        // is reported, and Yjs already coalesces keystrokes into transactions. Inert when
        // there is no Manager report hook (a session with no control channel).
        match reportName with
        | Some report ->
            let mutable lastReported = ""
            DocSync.onAnyUpdate doc (fun () ->
                match SyncedStateSync.ofDoc doc with
                | Ok synced ->
                    let title = (Ylmish.Text.toString synced.Title).Trim ()
                    if title <> "" && title <> lastReported then
                        lastReported <- title
                        Async.StartImmediate (report title)
                | Error _ -> ())
        | None -> ()

        // Manager→Session notifications (the reverse leg of the control RPC): subscribe
        // when the launch handed us a channel. The DEFAULT handler just logs — dispatching
        // a notification into a durable `SessionEvent` (via `log.Append`), or re-draining,
        // is left to whatever handler a later composition wants. A notification is a signal
        // the session MAY act on, never a fact it is obliged to persist.
        let mutable notifications : Subscription option = None
        match subscribeNotifications with
        | Some subscribe ->
            let handle (notification: SessionNotification) : unit =
                match notification with
                | EnvironmentChanged () ->
                    eprintfn "[session %s] notification: environment changed" (SessionId.value sessionId)
            notifications <- Some (subscribe handle)
        | None -> ()

        // The MCP server set (the second reverse leg, Plan 17): subscribe when the launch
        // handed us a channel. The current set arrives immediately, then a fresh WHOLE set on
        // every change — and with no attach step this is the ONLY way a session learns a
        // server exists, so what arrives here is held rather than logged.
        //
        // Terminals a previous process left open belong to a sandbox that died with it, so
        // the log is describing something that no longer exists. Close them before anything
        // reads the projection — and before the terminal drain, which must not try to run a
        // command in a terminal that is gone.
        do! terminals.ReconcileAtBoot ()
        // The boot half of the same arm: a completion the previous process never acted on is
        // still owed, and the wake re-derives it from the log rather than losing it.
        scheduler.Wake ()

        // The boot drain (Step 19): a replayed doc may hold entries that were pending
        // at the crash (consume them now) or already consumed but not yet removed (the
        // crash window — the log-anchored dedup repairs them without re-consuming).
        drain ()
        drainTerminals ()

        let mutable endWaiters : (unit -> unit) list = []
        let signalSessionEnded () =
            let waiters = endWaiters
            endWaiters <- []
            waiters |> List.iter (fun w -> w ())

        let onConnection (carrier: FrameChannel<string>) =
            // Supervised from the moment it arrives, before anything else can hold it: a peer
            // whose transport dies silently must reach the cleanup below — releasing its
            // terminal leases and clearing its cursor — rather than holding both for ever.
            // Wrapping here, not deeper, is what makes the relay map and the pump agree about
            // which channel this peer IS.
            let channel = Link.supervise Link.LinkPolicy.shipped carrier
            let connectionId = nextConnectionId
            nextConnectionId <- nextConnectionId + 1
            let handlers : PeerSession.PeerHandlers<string> =
                { OnState =
                    fun payload ->
                        DocSync.applyRemote doc payload
                        broadcastExcept connectionId payload
                  OnCommand =
                    SessionCommands.handle
                        requestInterrupt
                        terminals.Open
                        terminals.Close
                        terminals.Take
                        terminals.Release
                        terminals.Rearm
                        streamAttacher.Reattach
                        actorFor
                  OnPresence = fun payload -> broadcastPresenceExcept connectionId payload
                  // Live-mode traffic (Plan 13, stage 2e). Only the two peer-authored frames
                  // are acted on; a peer replaying a `TerminalRecord` or a `TerminalSnapshot`
                  // at us is asserting a fact about the transcript, which is the Process's
                  // alone to state, and is dropped exactly as a spoofed event page is.
                  OnTerminal =
                    fun peerId frame ->
                        let actor = actorFor peerId
                        match frame with
                        | TerminalInput (terminal, data) ->
                            // Dropped LOUDLY. A peer typing into a terminal it no longer holds
                            // sees nothing happen, and the only place that can be explained is
                            // here — most often a steal the sender has not learned about yet.
                            if not (terminals.Input terminal actor data) then
                                eprintfn
                                    "[session %s] terminal input dropped: %s does not hold %s"
                                    (SessionId.value sessionId)
                                    (PeerId.value peerId)
                                    (TerminalId.value terminal)
                        | TerminalResize (terminal, cols, rows) ->
                            terminals.Resize terminal actor cols rows |> ignore
                        | TerminalRecord _
                        | TerminalTranscriptAvailable _
                        | TerminalSnapshot _ -> ()
                  OnAccepted =
                    fun peerId ch ->
                        async {
                            connections <- Map.add connectionId ch connections
                            // Plan 11: an arriving peer makes this session busy NOW. A
                            // client that reconnects into an idle window must cancel the
                            // countdown without waiting up to a beat for it.
                            notifyActivity ()
                            do! ch.Send (State (StateSync (DocSync.fullState doc)))
                            // What each open terminal LOOKS like, and then how much of it
                            // there is. Both, and in that order, because they compose: the
                            // snapshot is where this client's screen starts, and the length
                            // is what sends it to fetch the lines from there. A hint with no
                            // origin to fold onto is a fetch with nowhere to put what it
                            // gets — which is what this was, because nothing sent the
                            // snapshot at all. `Screens.Sync` only folds records into an
                            // emulator a snapshot created, so every live terminal rendered
                            // an empty screen and a device looked like a panel with nothing
                            // in it.
                            for (terminal, length) in terminals.Lengths () do
                                match! terminals.Snapshot terminal with
                                | Some keyframe ->
                                    do! ch.Send (Terminal (TerminalSnapshot (terminal, keyframe)))
                                | None -> ()
                                do! ch.Send (Terminal (TerminalTranscriptAvailable (terminal, length)))
                            return
                                fun () ->
                                    connections <- Map.remove connectionId connections
                                    // Clear this peer's cursor on every remaining peer.
                                    broadcastPresenceExcept connectionId
                                        { PeerId = peerId; DisplayName = ""; Focus = None }
                                    // ...and release every terminal it was holding. A lease
                                    // held by someone who is gone is the one hold nobody
                                    // should have to clear by hand: without this a crashed
                                    // tab leaves the composer reading "nick is using this
                                    // terminal" for ever, with the queue held behind a peer
                                    // who cannot release it.
                                    Async.StartImmediate (terminals.PeerGone peerId)
                                    // ...and the last one leaving starts it, which is the
                                    // whole point: a closed tab should not cost a full beat
                                    // of idle time before it counts.
                                    notifyActivity ()
                        } }
            Async.StartImmediate(
                async {
                    do! PeerSession.run sessionId peerTokens.Validate log handlers channel
                    signalSessionEnded ()
                })

        // The event read surface: a client sends the offset it has folded
        // through, and this answers with the bounds of what came next — an address whose
        // bytes never change, so the client can keep it, the growing tail included.
        //
        // Both operations are `log.Read`, and the cursor deliberately reads the page it
        // then only reports the bounds of. The alternative is a length the log does not
        // expose, and the read is over an in-memory log: one extra read per NEW range is
        // not worth a second way to ask how long the log is.
        let encodeLines (page: EventPage<SessionEvent>) =
            page.Events |> List.map (Codec.toString Codec.sessionEventEnvelope)

        let eventsEndpoint : Signalling.EventsEndpoint =
            { ValidateToken = peerTokens.Validate >> Option.isSome
              BoundsAfter =
                fun after ->
                    async {
                        let! page = log.Read after EventChunk.size
                        // Empty means the caller is current. The bounds come from the
                        // events themselves rather than from arithmetic over the cursor,
                        // so a capped page and a short one are the same code path.
                        match page.Events, page.LastOffset with
                        | first :: _, Some last ->
                            return Some (EventOffset.value first.Offset, EventOffset.value last)
                        | _ -> return None
                    }
              ReadRange =
                fun first last ->
                    async {
                        let! after =
                            if first = 0L then async.Return None
                            else
                                async {
                                    match EventOffset.create (first - 1L) with
                                    | Ok o -> return Some o
                                    | Error e -> return failwithf "range bound invariant violated: %s" e
                                }
                        let wanted = int (last - first + 1L)
                        let! page = log.Read after wanted
                        let lines = encodeLines page
                        // Short means the log has not reached `last` yet. Answering with
                        // what exists would put a partial answer at an address that
                        // promises the whole range, and the client keeps that for ever.
                        return if List.length lines = wanted then Some lines else None
                    } }

        // The transcript read surface (Plan 13, cursored in Plan 22) — the history leg of the
        // terminal feed, on the same immutability argument as the event ranges. Built by the
        // store, because every decision in it is about the store's own state.
        let transcriptEndpoint =
            TranscriptStore.endpoint (peerTokens.Validate >> Option.isSome) transcripts

        let! server, closeConnections = Signalling.start sessionId onConnection (Some eventsEndpoint) (Some transcriptEndpoint) auth extraHttpRoutes peerTokens.Mint mount managerOrigin ephemeralStorage port
        // Port 0 asks the OS for a free port, so any number of instances/sessions
        // coexist; report the port actually bound.
        let port = Interop.serverPort server

        let waitForNextSessionEnd () : Async<unit> =
            // Register eagerly at call time so a session that ends before the await still
            // resolves the returned computation.
            let mutable ended = false
            let mutable waiter : (unit -> unit) option = None
            endWaiters <-
                (fun () ->
                    ended <- true
                    match waiter with
                    | Some w -> w ()
                    | None -> ())
                :: endWaiters
            async {
                return!
                    Async.FromContinuations(fun (cont, _, _) ->
                        if ended then cont () else waiter <- Some cont)
            }

        return
            { SessionId = sessionId
              MintPeerToken = fun () -> peerTokens.Mint UnattributedAccess
              MintPeerTokenAs = peerTokens.Mint
              Port = port
              Log = log
              Doc = doc
              Environment = environment
              Sandboxes = sandboxes
              Terminals = terminals
              SetCommandDispatch = fun table -> commandDispatch.Value <- table
              RunGated = commandGate.Run
              TerminalCommands = terminalCommands
              WaitForNextSessionEnd = waitForNextSessionEnd
              Connect = onConnection
              Stop =
                fun () ->
                    async {
                        clearInterval idleLeaseBeat
                        notifications |> Option.iter (fun s -> s.Stop ())
                        // Sandbox lifetime = session lifetime: take the WorkSandbox (and
                        // anything still running in it) down with the session.
                        do! sandboxes.StopAll ()
                        server.close ignore
                        // Drain every accepted peer connection: Stop resolves only once
                        // libdatachannel has reported each one closed, so a caller may
                        // follow with the global native teardown deterministically.
                        do! closeConnections ()
                    } }
    }

/// `startFull` without doc persistence — collaborative state is memory-only. No auth
/// gate on the HTTP surface (peer-token gating still applies at `PeerHello`); callers
/// mint peer tokens from the host handle.
let startWithEnvironment
    (runAgent: RunAgent option)
    (makeSandboxes: (EventLog<SessionEvent> -> WorkSandboxes.WorkSandboxes) option)
    (baseLog: EventLog<SessionEvent> option)
    (sessionId: SessionId)
    (port: int)
    : Async<SessionHost> =
    // No mount: these helpers serve an unfronted, origin-root session. No transcript store
    // either — terminals fall back to the in-memory one, which is the right default for a
    // host with no data directory.
    startFull (fun () -> runAgent) makeSandboxes None baseLog None None None None (fun _ _ -> ()) None McpClient.McpConnections.none None sessionId None "" None false port

/// `startWithEnvironment` without an environment — Step 08-era topology.
let startWith (runAgent: RunAgent option) (sessionId: SessionId) (port: int) : Async<SessionHost> =
    startWithEnvironment runAgent None None sessionId port

/// `startWith` without an agent — transport/draft/send scenarios that predate Step 08.
let start (sessionId: SessionId) (port: int) : Async<SessionHost> =
    startWith None sessionId port
