namespace Yession.Client

open Elmish
open Yjs
open Ylmish.Codec
open Yession.Domain

/// Composition of the Browser Client: the Elmish program bound to a Yjs document through
/// the Ylmish sync boundary, and the wiring of a connected `FrameChannel` to that
/// document. The Elmish model stays the single typed snapshot; only `ClientModel.Synced`
/// crosses into the doc (docs/design.md §1 "Ylmish is the sync boundary").
module App =

    /// Decode direction: read the synced state out of the doc, carrying every other
    /// model field through untouched (`Decode.ask` supplies the current model, so
    /// connection state, the conversation projection, etc. survive remote updates).
    let private decodeModel : Decoder<ClientModel, ClientModel> =
        Decode.object {
            let! model = Decode.ask
            let! synced = SyncedStateSync.decode
            return { model with Synced = synced }
        }

    /// The client Elmish program for a given Yjs doc: the pure `ClientModel.update`
    /// under `Program.withYlmish`, so local draft edits flow out as CRDT deltas and
    /// remote transactions fold back in as ordinary `Set` messages. `initial` is
    /// usually `ClientModel.init peer`.
    let makeProgram (doc: Y.Doc) (initial: ClientModel) =
        Program.mkProgram
            (fun () -> initial, Cmd.none)
            (fun msg model -> ClientModel.update msg model, Cmd.none)
            (fun _ _ -> ())
        |> Ylmish.Program.withYlmish
            { Doc = doc
              Create = fun (m: ClientModel) -> SyncedStateSync.create m.Synced
              Update = fun a m -> SyncedStateSync.update a m.Synced
              Encode = SyncedStateSync.encode
              Decode = decodeModel
              OnError = Ylmish.Program.OnError.log }

    /// A wired client connection: the frame pump to run, plus the actions that speak
    /// over it.
    type Connection =
        { /// Runs the handshake and frame pump until the channel closes.
          Run : Async<unit>
          /// Send a draft: enqueue it (Phase 3). A pure CRDT model update under a
          /// freshly minted `QueueId` — no command round-trip; the Session Process
          /// consumes the queue and the message lands in the timeline as events.
          SendDraft : DraftId -> unit
          /// Ask the Session Process to cancel the running agent turn (Step 17). The
          /// outcome arrives as events: `AgentTurnInterrupted` on success, or nothing
          /// if the turn already finished (the request is then rejected).
          InterruptTurn : AgentTurnId -> unit }

    /// How a connection consumes the event log (Step 07).
    type ConnectOptions =
        { /// Resume consumption after this offset (the model's `LastProcessedOffset`
          /// when reconnecting); `None` reads from the beginning.
          ResumeAfter : EventOffset option
          /// Events per `ReadEventsAfter` request.
          PageSize : int }

    module ConnectOptions =
        let defaults : ConnectOptions = { ResumeAfter = None; PageSize = 100 }

    /// Wire a connected channel to the client's doc and the event log: locally-originated
    /// doc updates (the Ylmish binding's writes) are sent as `State` frames, inbound
    /// `State` payloads are applied to the doc, command responses are correlated back to
    /// their drafts, and the handshake/lifecycle frames drive `dispatch`.
    ///
    /// Event consumption is read-only and offset-driven: `EventsAvailable` hints (and the
    /// accepted handshake's latest offset) only trigger `ReadEventsAfter` requests; the
    /// returned pages are the source of truth and are folded into the model as
    /// `EventsPageMsg`. One read is in flight at a time; a non-final page immediately
    /// requests the next. The doc listener is registered before the pump starts so no
    /// local update can be missed.
    let connect
        (options: ConnectOptions)
        (doc: Y.Doc)
        (hello: PeerHelloPayload)
        (dispatch: ClientMsg -> unit)
        (channel: FrameChannel<string>)
        : Connection =
        DocSync.onLocalUpdate doc (fun payload ->
            Async.StartImmediate (channel.Send (State (StateSync payload))))

        // No commands are issued by the client anymore (sending is a CRDT write);
        // responses to any future commands are currently uncorrelated.
        let onResponse (_requestId: RequestId) (_result: SessionCommandResult) = ()

        // The consumption loop's own read position (seeded for reconnect catch-up).
        let mutable lastProcessed : EventOffset option = options.ResumeAfter
        let mutable latestKnown : EventOffset option = None
        let mutable readInFlight : RequestId option = None

        let behind () =
            match latestKnown, lastProcessed with
            | Some latest, Some processed -> EventOffset.value latest > EventOffset.value processed
            | Some _, None -> true
            | None, _ -> false

        let request () =
            let requestId = RequestId.fresh ()
            readInFlight <- Some requestId
            Async.StartImmediate (channel.Send (EventLog (ReadEventsAfter (requestId, lastProcessed, options.PageSize))))

        let requestIfBehind () =
            if Option.isNone readInFlight && behind () then request ()

        let onEventsPage (requestId: RequestId) (page: EventPage<SessionEvent>) =
            if readInFlight = Some requestId then readInFlight <- None
            lastProcessed <- EventOffset.maxOption lastProcessed page.LastOffset
            latestKnown <- EventOffset.maxOption latestKnown page.LastOffset
            dispatch (EventsPageMsg page)
            // A non-final page means more events already exist beyond this one.
            if not page.IsEnd && Option.isNone readInFlight then request ()
            else requestIfBehind ()

        let dispatchAndConsume (msg: ClientMsg) =
            dispatch msg
            match msg with
            | ConnectedMsg accepted ->
                // The client's half of the initial full-state exchange: state restored
                // from local persistence (Step 20) — or carried across a reconnect —
                // predates the update listener, so push it explicitly. Full-state
                // updates are idempotent, so this is always safe.
                Async.StartImmediate (channel.Send (State (StateSync (DocSync.fullState doc))))
                latestKnown <- EventOffset.maxOption latestKnown accepted.LatestOffset
                requestIfBehind ()
            | EventsAvailableMsg latest ->
                latestKnown <- EventOffset.maxOption latestKnown (Some latest)
                requestIfBehind ()
            | _ -> ()

        { Run =
            Connection.run hello dispatchAndConsume (DocSync.applyRemote doc) onResponse onEventsPage channel
          SendDraft =
            fun draftId ->
                // Enqueue under a fresh queue id (unique keys make concurrent sends
                // safe); the model update moves the draft into the shared queue.
                match QueueId.create (string (System.Guid.NewGuid ())) with
                | Ok queueId -> dispatch (SendDraftMsg (draftId, queueId))
                | Error e -> failwithf "queue id invariant violated: %s" e
          InterruptTurn =
            fun turnId ->
                Async.StartImmediate (
                    channel.Send (Command (Request (RequestId.fresh (), InterruptAgentTurn turnId)))) }
