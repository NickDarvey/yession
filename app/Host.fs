module Yession.Host.Host

// The Session Process host: owns the event log and accepts WebRTC peer connections,
// running the token-gated peer-session handshake for each. This is the composition root
// for the running process (design.md §1 "Composition at the top", §2.1).

open System
open Yjs
open Yession.Domain
open Yession.SessionProcess

/// The scheduler's record of the running agent turn (Step 17): identity for interrupt
/// validation, a generation for slot-release checks, and the abort plumbing.
type private RunningTurn =
    { Generation : int
      TurnId : AgentTurnId
      mutable Aborted : bool
      mutable AbortCallbacks : (unit -> unit) list }

type SessionHost =
    { SessionId : SessionId
      Token : string
      Port : int
      Log : EventLog<SessionEvent>
      /// The session's Yjs document. The Session Process owns it; peers hold replicas
      /// synced over `State` frames.
      Doc : Y.Doc
      /// The session's lazily-started environment (Step 12).
      Environment : SessionEnvironment.SessionEnvironment
      /// Resolves when the next peer session ends. Register (call) it *before* triggering
      /// the disconnect you want to observe, then await it — this avoids any reliance on
      /// timing to see the resulting `PeerLeft`.
      WaitForNextSessionEnd : unit -> Async<unit>
      Stop : unit -> Async<unit> }

/// Start a Session Process: create the event log and the session's Yjs document, start
/// HTTP bootstrap + signalling, and run a peer session for every connection. Each
/// accepted peer receives the full doc state, then incremental updates are relayed
/// between peers through the doc. The Process is the single consumer of the shared
/// message queue: when the agent is idle it drains the queue into `MessageSent` events
/// and — when `runAgent` is given — starts one coalesced turn per batch (Phase 3).
/// When `environmentCapabilities` is given (granted by the Session Manager, Step 11),
/// the session gets a lazily-started environment (Step 12). Resolves once the server
/// is listening.
let startWithCapabilities
    (runAgent: RunAgent option)
    (environmentCapabilities: SessionEnvironmentCapabilities option)
    (baseLog: EventLog<SessionEvent> option)
    (sessionId: SessionId)
    (token: string)
    (port: int)
    : Async<SessionHost> =
    async {
        let doc = Y.Doc.Create ()

        // Connected peers' channels, for state relay; keyed per connection.
        let mutable connections : Map<int, FrameChannel<string>> = Map.empty
        let mutable nextConnectionId = 0

        let sendState (channel: FrameChannel<string>) (payload: string) =
            Async.StartImmediate (channel.Send (State (StateSync payload)))

        let broadcastExcept (except: int) (payload: string) =
            connections |> Map.iter (fun id channel -> if id <> except then sendState channel payload)

        // Every durable fact is advertised: appends go through a log wrapper that
        // broadcasts the new latest offset to all connected peers (clients page the
        // actual events in Step 07).
        let broadcastEventsAvailable (offset: EventOffset) =
            connections
            |> Map.iter (fun _ channel ->
                Async.StartImmediate (channel.Send (EventLog (EventsAvailable offset))))

        // The drain's log-anchored dedup set: every QueueId already named by a
        // MessageSent. Seeded from the durable log below (the restart case) and
        // maintained on every append, so exactly-once is anchored in the log, not the
        // doc (docs/plans/01-turn-scheduling.md).
        let mutable consumedQueueIds : Set<string> = Set.empty

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
                            let! appended = inner.Append actor event
                            match QueueDrain.consumedOf event with
                            | Some key -> consumedQueueIds <- Set.add key consumedQueueIds
                            | None -> ()
                            broadcastEventsAvailable appended.Offset
                            return appended
                        } }

        let! replayed = log.Read None Int32.MaxValue
        consumedQueueIds <- replayed.Events |> List.choose (fun e -> QueueDrain.consumedOf e.Event) |> Set.ofList

        // The session's environment: lazily started through the Manager-granted scoped
        // capability; absent capability, needs are recorded as unavailable.
        let environment =
            match environmentCapabilities with
            | Some capabilities ->
                SessionEnvironment.create
                    log
                    capabilities
                    EnvironmentSpec.localProcess
                    (sprintf "env-%s" (SessionId.value sessionId))
            | None -> SessionEnvironment.unavailable

        let mintTurnId () =
            match AgentTurnId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "agent turn id invariant violated: %s" e
        let mintMessageId () =
            match MessageId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "message id invariant violated: %s" e
        let capabilitiesFor (turnId: AgentTurnId) : AgentCapabilities =
            { EnsureEnvironment = environment.Ensure (Some turnId)
              ExecuteCommand = environment.Execute }

        // The queue drain (Phase 3): the Session Process is the queue's single
        // consumer, and this drain is the linearization point of "accepted by the
        // agent". While a turn runs, sends accumulate in the queue (Cursor's default);
        // when idle, the queue drains: durable append first, doc removal second, then
        // ONE coalesced turn for the whole batch. The snapshot/append/removal block
        // never yields to IO (synchronous log append), so the drain is atomic on the
        // process tick.
        //
        // Single-flight (Step 17-aware): `running` is the turn slot; an interrupt
        // clears it early and starts the next turn immediately, so the interrupted
        // turn's own return must NOT release its successor's slot — the generation
        // check below decides. `drainBusy` guards the pre-turn synchronous section.
        let mutable generation = 0
        let mutable running : RunningTurn option = None
        let mutable drainBusy = false

        let signalFor (turn: RunningTurn) : AgentAbortSignal =
            { IsAborted = fun () -> turn.Aborted
              OnAbort =
                fun callback ->
                    if turn.Aborted then callback ()
                    else turn.AbortCallbacks <- callback :: turn.AbortCallbacks }

        let rec drain () =
            if drainBusy || Option.isSome running then () else
            match SyncedStateSync.ofDoc doc with
            | Error _ -> ()
            | Ok synced when Map.isEmpty synced.Queue -> ()
            | Ok synced ->
                let plan = QueueDrain.plan consumedQueueIds synced.Queue
                if List.isEmpty plan.Batch then
                    // Everything present is already in the log (a crash between
                    // append and removal): repair the doc, consume nothing twice.
                    SyncedStateSync.removeQueued doc plan.Removals
                else
                    drainBusy <- true
                    Async.StartImmediate (
                        async {
                            // 1. Durable: each consumed entry becomes an immutable
                            //    MessageSent (body snapshotted from this replica,
                            //    QueueId as the exactly-once anchor) BEFORE the doc
                            //    removal — a crash here leaves only a repairable
                            //    leftover, never a lost or doubled message.
                            let mutable lastMessage : MessageSent option = None
                            for entry in plan.Batch do
                                let message =
                                    { MessageId = mintMessageId ()
                                      DraftId = None
                                      QueueId = Some entry.QueueId
                                      Author = HumanPeer entry.Author
                                      Body = Ylmish.Text.toString entry.Body }
                                let! _ = log.Append (HumanPeer entry.Author) (MessageSent message)
                                lastMessage <- Some message
                            // 2. Visible: one transaction under the process origin;
                            //    the removal relays to every peer like any update.
                            SyncedStateSync.removeQueued doc plan.Removals
                            // 3. Run one coalesced turn, triggered by the batch tail.
                            match runAgent, lastMessage with
                            | Some agent, Some trigger ->
                                generation <- generation + 1
                                let turn =
                                    { Generation = generation
                                      TurnId = mintTurnId ()
                                      Aborted = false
                                      AbortCallbacks = [] }
                                running <- Some turn
                                drainBusy <- false
                                let! page = log.Read None Int32.MaxValue
                                let projection, _ =
                                    ConversationProjection.applyEvents None page.Events ConversationProjection.empty
                                do! AgentTurn.run log agent (signalFor turn) capabilitiesFor (fun () -> turn.TurnId) mintMessageId sessionId projection.Items trigger
                                // Release the slot and re-arm — unless an interrupt
                                // already released it (and possibly started a successor).
                                match running with
                                | Some current when current.Generation = turn.Generation ->
                                    running <- None
                                    drain ()
                                | _ -> ()
                            | _ ->
                                drainBusy <- false
                                // Re-arm: anything enqueued during the appends drains now.
                                drain ()
                        })

        // The interrupt authority (Step 17), injected into the command handler: valid
        // only for the turn currently running (the interrupt-vs-completion race is
        // decided right here). Terminal event first (durable), then the abort signal,
        // then an immediate drain — queued messages start their turn now.
        let requestInterrupt (peerId: PeerId) (turnId: AgentTurnId) : Result<unit, string> =
            match running with
            | Some turn when turn.TurnId = turnId ->
                turn.Aborted <- true
                let callbacks = turn.AbortCallbacks
                turn.AbortCallbacks <- []
                // Release the slot BEFORE firing the callbacks: an abort that resumes
                // the turn synchronously must find its generation already retired, or
                // its own completion path would release (and re-drain) a second time.
                running <- None
                Async.StartImmediate (
                    async {
                        let! _ =
                            log.Append
                                (HumanPeer peerId)
                                (AgentTurnInterrupted { AgentTurnId = turnId; RequestedBy = peerId })
                        return ()
                    })
                callbacks |> List.iter (fun callback -> callback ())
                drain ()
                Ok ()
            | _ -> Error "turn already finished"

        // Process-originated doc writes (the drain's queue removals) broadcast to every
        // peer; peer payloads are relayed by the receiving connection.
        DocSync.onLocalUpdate doc (fun payload ->
            connections |> Map.iter (fun _ channel -> sendState channel payload))

        // Durable facts from collaborative state: the first appearance of a draft in the
        // doc appends `DraftStarted` exactly once. Content stays in Yjs; the log records
        // only the fact (docs/design.md §1 "Durable facts are events").
        let mutable knownDrafts : Set<string> = Set.empty
        DocSync.onAnyUpdate doc (fun () ->
            match SyncedStateSync.ofDoc doc with
            | Ok synced ->
                synced.Drafts
                |> Map.iter (fun draftId draft ->
                    let key = DraftId.value draftId
                    if not (Set.contains key knownDrafts) then
                        knownDrafts <- Set.add key knownDrafts
                        Async.StartImmediate (
                            async {
                                let! _ =
                                    log.Append
                                        (HumanPeer draft.Author)
                                        (DraftStarted { DraftId = draftId; StartedBy = draft.Author })
                                return ()
                            }))
            // Schema drift in the doc must not break relay; decode errors are ignored here.
            | Error _ -> ()
            // The drain re-arms on every doc update observed while idle, so an enqueue
            // can never be missed (liveness; recursion during a drain's own removal is
            // cut by the single-flight guard).
            drain ())

        let mutable endWaiters : (unit -> unit) list = []
        let signalSessionEnded () =
            let waiters = endWaiters
            endWaiters <- []
            waiters |> List.iter (fun w -> w ())

        let onConnection (channel: FrameChannel<string>) =
            let connectionId = nextConnectionId
            nextConnectionId <- nextConnectionId + 1
            let handlers : PeerSession.PeerHandlers<string> =
                { OnState =
                    fun payload ->
                        DocSync.applyRemote doc payload
                        broadcastExcept connectionId payload
                  OnCommand = SessionCommands.handle requestInterrupt
                  OnAccepted =
                    fun _ ch ->
                        async {
                            connections <- Map.add connectionId ch connections
                            do! ch.Send (State (StateSync (DocSync.fullState doc)))
                            return fun () -> connections <- Map.remove connectionId connections
                        } }
            Async.StartImmediate(
                async {
                    do! PeerSession.run sessionId token log handlers channel
                    signalSessionEnded ()
                })

        let! server = Signalling.start onConnection port
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
              Token = token
              Port = port
              Log = log
              Doc = doc
              Environment = environment
              WaitForNextSessionEnd = waitForNextSessionEnd
              Stop = fun () -> async { server.close ignore } }
    }

/// `startWithCapabilities` without an environment — Step 08-era topology.
let startWith (runAgent: RunAgent option) (sessionId: SessionId) (token: string) (port: int) : Async<SessionHost> =
    startWithCapabilities runAgent None None sessionId token port

/// `startWith` without an agent — transport/draft/send scenarios that predate Step 08.
let start (sessionId: SessionId) (token: string) (port: int) : Async<SessionHost> =
    startWith None sessionId token port
