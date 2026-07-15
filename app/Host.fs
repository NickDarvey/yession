module Yession.Host.Host

// The Session Process host: owns the event log and accepts WebRTC peer connections,
// running the token-gated peer-session handshake for each. This is the composition root
// for the running process (design.md §1 "Composition at the top", §2.1).

open System
open Yjs
open Yession.Domain
open Yession.SessionProcess

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
/// the session gets a lazily-started environment (Step 12). When `docStore` is given
/// (Step 19), the persisted doc is replayed at boot — unsent queue entries and drafts
/// survive restarts — and every subsequent update is durably appended. Resolves once
/// the server is listening.
let startFull
    (runAgent: RunAgent option)
    (environmentCapabilities: SessionEnvironmentCapabilities option)
    (baseLog: EventLog<SessionEvent> option)
    (docStore: DocStore.DocStore option)
    (sessionId: SessionId)
    (token: string)
    (port: int)
    : Async<SessionHost> =
    async {
        let doc = Y.Doc.Create ()
        // Restart ordering (Step 19): replay the persisted doc FIRST — before any
        // observer registers — then open the log, then the boot drain repairs the
        // crash-between-append-and-removal window via the log-anchored dedup.
        docStore |> Option.iter (fun store -> store.ReplayInto doc)
        // The persistence tap registers before every other observer, so an update is
        // durable before anything acts on it.
        docStore |> Option.iter (fun store -> DocSync.onAnyUpdatePayload doc store.Append)

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
                            broadcastEventsAvailable appended.Offset
                            return appended
                        } }

        // Seed the scheduler's log-anchored dedup set from the durable log (the
        // restart case): exactly-once is anchored in the log, not the doc.
        let! replayed = log.Read None Int32.MaxValue
        let initialConsumed =
            replayed.Events |> List.choose (fun e -> QueueDrain.consumedOf e.Event) |> Set.ofList

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

        // The queue drain and turn scheduler (Phase 3) — the real machinery lives in
        // `Scheduler` (shared with the property harness); the Host wires it to this
        // session's doc, log, environment capabilities, and command surface.
        let scheduler =
            Scheduler.create sessionId doc log runAgent capabilitiesFor mintTurnId mintMessageId initialConsumed
        let drain () = scheduler.Drain ()
        let requestInterrupt = scheduler.RequestInterrupt

        // Process-originated doc writes (the drain's queue removals) broadcast to every
        // peer; peer payloads are relayed by the receiving connection.
        DocSync.onLocalUpdate doc (fun payload ->
            connections |> Map.iter (fun _ channel -> sendState channel payload))

        // Durable facts from collaborative state: the first appearance of a draft in the
        // doc appends `DraftStarted` exactly once. Content stays in Yjs; the log records
        // only the fact (docs/design.md §1 "Durable facts are events").
        // Seeded from the replayed log so drafts restored by doc replay (Step 19) are
        // not re-announced as started.
        let mutable knownDrafts : Set<string> =
            replayed.Events
            |> List.choose (fun e ->
                match e.Event with
                | DraftStarted d -> Some (DraftId.value d.DraftId)
                | _ -> None)
            |> Set.ofList
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

        // The boot drain (Step 19): a replayed doc may hold entries that were pending
        // at the crash (consume them now) or already consumed but not yet removed (the
        // crash window — the log-anchored dedup repairs them without re-consuming).
        drain ()

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

        // The HTTP-cacheable event read surface: chunk n = the JSONL envelopes at
        // offsets [n*size, (n+1)*size). Full chunks are immutable (append-only log),
        // so browsers cache them hard and cold loads replay history from disk.
        let eventsEndpoint : Signalling.EventsEndpoint =
            { Token = token
              ReadChunk =
                fun index ->
                    async {
                        let after =
                            if index = 0 then None
                            else
                                match EventOffset.create (EventChunk.firstOffset index - 1L) with
                                | Ok o -> Some o
                                | Error e -> failwithf "chunk offset invariant violated: %s" e
                        let! page = log.Read after EventChunk.size
                        let lines = page.Events |> List.map (Codec.toString Codec.sessionEventEnvelope)
                        return lines, List.length lines = EventChunk.size
                    } }

        let! server = Signalling.start sessionId onConnection (Some eventsEndpoint) port
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

/// `startFull` without doc persistence — collaborative state is memory-only.
let startWithCapabilities
    (runAgent: RunAgent option)
    (environmentCapabilities: SessionEnvironmentCapabilities option)
    (baseLog: EventLog<SessionEvent> option)
    (sessionId: SessionId)
    (token: string)
    (port: int)
    : Async<SessionHost> =
    startFull runAgent environmentCapabilities baseLog None sessionId token port

/// `startWithCapabilities` without an environment — Step 08-era topology.
let startWith (runAgent: RunAgent option) (sessionId: SessionId) (token: string) (port: int) : Async<SessionHost> =
    startWithCapabilities runAgent None None sessionId token port

/// `startWith` without an agent — transport/draft/send scenarios that predate Step 08.
let start (sessionId: SessionId) (token: string) (port: int) : Async<SessionHost> =
    startWith None sessionId token port
