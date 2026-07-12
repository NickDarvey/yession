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
/// between peers through the doc. When `runAgent` is given, every human `MessageSent`
/// triggers exactly one agent turn whose lifecycle is appended as events (Step 08).
/// When `environmentCapabilities` is given (granted by the Session Manager, Step 11),
/// the session gets a lazily-started environment (Step 12). Resolves once the server
/// is listening.
let startWithCapabilities
    (runAgent: RunAgent option)
    (environmentCapabilities: SessionEnvironmentCapabilities option)
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

        // Filled in below (the trigger needs the wrapped log, which needs the trigger).
        let mutable onAppended : SessionEvent -> unit = ignore

        let log =
            let inner = InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)
            { inner with
                Append =
                    fun actor event ->
                        async {
                            let! appended = inner.Append actor event
                            broadcastEventsAvailable appended.Offset
                            onAppended event
                            return appended
                        } }

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

        // A human MessageSent triggers exactly one agent turn (Step 08). The agent's
        // context is projected from the event log alone; its own events carry
        // ActorRef.Agent authorship, so they can never re-trigger a turn. The turn's
        // typed capabilities (Step 12) route through the session environment.
        match runAgent with
        | Some agent ->
            onAppended <-
                fun event ->
                    match event with
                    | MessageSent message when (match message.Author with HumanPeer _ -> true | _ -> false) ->
                        Async.StartImmediate (
                            async {
                                let! page = log.Read None Int32.MaxValue
                                let projection, _ =
                                    ConversationProjection.applyEvents None page.Events ConversationProjection.empty
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
                                      ExecuteCommand =
                                        fun _ -> async { return CommandExecutionFailed "command execution lands in Step 13" } }
                                do! AgentTurn.run log agent capabilitiesFor mintTurnId mintMessageId sessionId projection.Items message
                            })
                    | _ -> ()
        | None -> ()

        // Process-originated doc writes (none yet — the Process is doc-read-only)
        // broadcast to every peer; peer payloads are relayed by the receiving connection.
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
            | Error _ -> ())

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
                  OnCommand =
                    SessionCommands.handle
                        (fun () -> SyncedStateSync.ofDoc doc |> Result.mapError (sprintf "%A"))
                        log
                        (fun () ->
                            match MessageId.create (string (Guid.NewGuid ())) with
                            | Ok id -> id
                            | Error e -> failwithf "message id invariant violated: %s" e)
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
    startWithCapabilities runAgent None sessionId token port

/// `startWith` without an agent — transport/draft/send scenarios that predate Step 08.
let start (sessionId: SessionId) (token: string) (port: int) : Async<SessionHost> =
    startWith None sessionId token port
