namespace Yession.App

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
    /// remote transactions fold back in as ordinary `Set` messages. The view is supplied
    /// by `Program.withSetState` (the browser renders `View.view` with Lit; the headless
    /// test harness captures the model), so the program itself carries a unit view.
    /// `initial` is usually `ClientModel.init peer`.
    let makeProgram (doc: Y.Doc) (initial: ClientModel) =
        Program.mkProgram
            (fun () -> initial, Cmd.none)
            (fun msg model -> ClientModel.update msg model, Cmd.none)
            (fun _ _ -> ())
        |> Ylmish.Program.withYlmish
            { Doc = doc
              Create = fun (m: ClientModel) -> SyncedStateSync.create m.Synced
              Update = fun a m -> SyncedStateSync.update a m.Synced
              // Rich bodies are NOT encoded here — they are sibling `Y.XmlFragment` roots the
              // app manages directly (RichText.fs), so the sync boundary carries only structure.
              Encode = SyncedStateSync.encode
              Decode = decodeModel
              OnError = Ylmish.Program.OnError.log }

    /// A wired client connection: the frame pump to run, plus the actions that speak
    /// over it.
    type Connection =
        { /// Runs the handshake and frame pump until the channel closes.
          Run : Async<unit>
          /// Send the draft in the given peer's slot: enqueue it (Phase 3). Owner-sends,
          /// so the `PeerId` is the local peer's own slot. A pure CRDT model update under a
          /// freshly minted `QueueId` — no command round-trip; the Session Process
          /// consumes the queue and the message lands in the timeline as events.
          SendDraft : PeerId -> unit
          /// Ask the Session Process to cancel the running agent turn (Step 17). The
          /// outcome arrives as events: `AgentTurnInterrupted` on success, or nothing
          /// if the turn already finished (the request is then rejected).
          InterruptTurn : AgentTurnId -> unit
          /// Broadcast the local peer's caret+selection focus (or `None` when it leaves every
          /// collaborative field), so collaborators see the cursor. Ephemeral presence — the
          /// Session Process relays it to other peers and never persists it.
          ReportPresence : Focus option -> unit }

    /// The platform's HTTP GET, as a TOTAL function: the body, the status it refused with,
    /// or the transport error it never got past. Totality is the whole point — the old port
    /// threw, and a thrown fetch was indistinguishable from "the log has nothing new".
    type HttpFailure =
        | HttpStatus of status: int
        | HttpUnreachable of detail: string

    type HttpGet = string -> Async<Result<string, HttpFailure>>

    /// Why an event-feed read failed. The cases exist to be classified: a refused
    /// connection may well come back, an unauthorized read needs a fresh login, and a chunk
    /// that does not decode is corruption. Retrying helps exactly one of those.
    type FeedFault =
        | FeedUnreachable of detail: string
        | FeedRefused of status: int
        | FeedCorrupt of detail: string

    module FeedFault =

        /// Is retrying capable of helping? (Polly's "handle" clause, as a total function.)
        let isTransient =
            function
            | FeedUnreachable _ -> true
            // 5xx is a Session Process in trouble; 408/429 are it asking to be left alone
            // briefly. Every other status is a decision, not a hiccup.
            | FeedRefused status -> status >= 500 || status = 408 || status = 429
            | FeedCorrupt _ -> false

        /// A short reason, for the degraded feed's line in the UI.
        let describe =
            function
            | FeedUnreachable detail -> if detail = "" then "unreachable" else detail
            | FeedRefused status when status = 401 || status = 403 -> "not authorized"
            | FeedRefused status -> sprintf "HTTP %d" status
            | FeedCorrupt detail -> sprintf "corrupt chunk: %s" detail

    /// Reading event pages over something other than frames: the browser's HTTP chunk feed.
    /// Failure is in the type, so the read loop can tell a dead feed from an empty one — and
    /// so a resilience policy can be composed onto it without either end knowing.
    type EventFeed = EventOffset option -> Async<Result<EventPage<SessionEvent>, FeedFault>>

    /// How a connection consumes the event log (Step 07).
    type ConnectOptions =
        { /// Resume consumption after this offset (the model's `LastProcessedOffset`
          /// when reconnecting); `None` reads from the beginning.
          ResumeAfter : EventOffset option
          /// Events per `ReadEventsAfter` request.
          PageSize : int
          /// When given, event pages are fetched through this feed instead of
          /// `ReadEventsAfter` frames — the browser passes its HTTP chunk fetcher here
          /// so the browser cache serves history (immutable full chunks); pure
          /// data-channel peers leave it `None` and read over frames.
          FetchEvents : EventFeed option }

    module ConnectOptions =
        let defaults : ConnectOptions = { ResumeAfter = None; PageSize = 100; FetchEvents = None }

    /// The HTTP event feed for `ConnectOptions.FetchEvents`: translates "events after
    /// offset X" into the Session Process's immutable-chunk URL scheme (`/events/{n}`) and
    /// decodes the JSONL envelopes. Because full chunks are served immutable, an HTTP cache
    /// in front of `get` (the browser's) makes history replay local; only the growing tail
    /// chunk hits the network. Also home to the shipped resilience policy for that feed —
    /// the policy is a value here so the composition site is one line and the TEST runs the
    /// same policy that ships.
    module EventFetch =

        /// Build a feed over the platform's HTTP GET. `token` = a session-minted peer token
        /// appended as `?token=` — the cookie-less path (Node clients); the browser passes
        /// None and rides its same-origin auth cookie.
        ///
        /// Every failure is a `FeedFault`, never a fabricated page and never an exception:
        /// a transport error is `FeedUnreachable`, a refusal keeps its status (so 401 and
        /// 503 can be told apart), and a line that will not decode is `FeedCorrupt`. This
        /// function does not retry — `Resilience.Policy.guard` does, wrapped around it.
        let overHttp (get: HttpGet) (baseUrl: string) (token: string option) : EventFeed =
            fun after ->
                async {
                    let nextOffset =
                        match after with
                        | Some o -> EventOffset.value o + 1L
                        | None -> 0L
                    let tokenSuffix =
                        token
                        |> Option.map (fun t -> sprintf "?token=%s" (System.Uri.EscapeDataString t))
                        |> Option.defaultValue ""
                    let url =
                        sprintf "%s/events/%d%s" baseUrl (EventChunk.indexOf nextOffset) tokenSuffix
                    match! get url with
                    | Error (HttpUnreachable detail) -> return Error (FeedUnreachable detail)
                    | Error (HttpStatus status) -> return Error (FeedRefused status)
                    | Ok text ->
                        let lines = text.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
                        // Stop at the FIRST line that will not decode: a partially decoded
                        // chunk is not a page, it is a guess.
                        let rec decode remaining acc =
                            match remaining with
                            | [] -> Ok (List.rev acc)
                            | line :: rest ->
                                match Codec.fromString Codec.sessionEventEnvelope line with
                                | Ok envelope -> decode rest (envelope :: acc)
                                | Error e -> Error (FeedCorrupt e)
                        match decode (List.ofArray lines) [] with
                        | Error fault -> return Error fault
                        | Ok envelopes ->
                            let fresh =
                                envelopes
                                |> List.filter (fun e ->
                                    match after with
                                    | Some o -> EventOffset.value e.Offset > EventOffset.value o
                                    | None -> true)
                            return
                                Ok
                                    { Events = fresh
                                      LastOffset = fresh |> List.tryLast |> Option.map (fun e -> e.Offset)
                                      // A full chunk means more may exist beyond it; a partial
                                      // chunk IS the log's current tail.
                                      IsEnd = Array.length lines < EventChunk.size }
                }

        /// The shipped resilience policy for the HTTP feed: five retries, exponentially
        /// backed off from 250ms with a 10s ceiling, jittered by up to half so a Session
        /// Process restart does not bring every peer back in lockstep, and applied only to
        /// faults retrying can fix. `sleep` and `random` are parameters so the policy a test
        /// drives is the policy that ships — the schedule is fixed here, the clock is not.
        let policy
            (sleep: System.TimeSpan -> Async<unit>)
            (random: unit -> float)
            (observe: Resilience.Attempt<FeedFault> -> unit)
            : Resilience.Policy<FeedFault> =
            { Schedule =
                Resilience.Schedule.exponential
                    (System.TimeSpan.FromMilliseconds 250.0)
                    2.0
                    (System.TimeSpan.FromSeconds 10.0)
                    5
                |> Resilience.Schedule.jittered 0.5 random
              Retryable = FeedFault.isTransient
              Sleep = sleep
              Observe = observe }

        /// The health a failed attempt implies WHILE the policy is still trying. A final
        /// failure is deliberately absent here: that reaches the model from the read loop,
        /// the one place that knows the read is over — so the two never race to describe the
        /// same fact. Composition site: `policy … (retrying >> Option.iter (EventFeedMsg >> dispatch))`.
        let retrying (attempt: Resilience.Attempt<FeedFault>) : FeedHealth option =
            attempt.Retrying
            |> Option.map (fun _ -> FeedRetrying (attempt.Number, FeedFault.describe attempt.Error))

    /// Opening the session transport. The browser's WebRTC handshake is a promise that used
    /// to settle ONLY on success, so a session that never answered left the shell in
    /// `Connecting` forever — the same silent dead end the event feed had, one leg over. As a
    /// total function it either yields a channel or says why not.
    type ChannelFault =
        | ChannelUnreachable of detail: string
        | ChannelTimedOut

    module ChannelFault =

        let describe =
            function
            | ChannelUnreachable detail -> if detail = "" then "session unreachable" else detail
            | ChannelTimedOut -> "the session did not answer"

    module SessionChannel =

        /// The shipped policy for opening the transport: four retries, exponentially backed
        /// off from 500ms to a 15s ceiling, jittered. EVERY fault is retryable here, and that
        /// is not laziness — the only faults this port can produce mean "the session is not
        /// there yet", and a Session Process that is restarting comes back. Authorization is
        /// settled before this point (`/me`) and peer admission after it (the hello
        /// handshake), so neither is in scope for a retry decision.
        ///
        /// Nothing observes the attempts: while they run the model is `Connecting`, which is
        /// the whole truth. Interim reporting earns its place on the feed, where the
        /// alternative is a timeline that silently stops filling; here it would be noise.
        let policy
            (sleep: System.TimeSpan -> Async<unit>)
            (random: unit -> float)
            : Resilience.Policy<ChannelFault> =
            { Schedule =
                Resilience.Schedule.exponential
                    (System.TimeSpan.FromMilliseconds 500.0)
                    2.0
                    (System.TimeSpan.FromSeconds 15.0)
                    4
                |> Resilience.Schedule.jittered 0.5 random
              Retryable = fun _ -> true
              Sleep = sleep
              Observe = ignore }

    /// Wire a connected channel to the client's doc and the event log: locally-originated
    /// doc updates (the Ylmish binding's writes) are sent as `State` frames, inbound
    /// `State` payloads are applied to the doc, command responses are correlated back to
    /// their drafts, and the handshake/lifecycle frames drive `dispatch`.
    ///
    /// Event consumption is read-only and offset-driven: `EventsAvailable` hints (and the
    /// accepted handshake's latest offset) only trigger `ReadEventsAfter` requests; the
    /// returned pages are the source of truth and are folded into the model as
    /// `EventsPageMsg`. One read is in flight at a time; a non-final page immediately
    /// requests the next. A read that FAILS (only possible over a `FetchEvents` feed, which
    /// has already exhausted its resilience policy) parks the loop and reports
    /// `FeedStalled` — it never masquerades as an empty page. The doc listener is registered
    /// before the pump starts so no local update can be missed.
    let connect
        (options: ConnectOptions)
        (doc: Y.Doc)
        (registry: BodyRegistry)
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

        // `request` delivers fetched pages back through `onEventsPage`, which may in
        // turn request the next page — hence the mutual recursion.
        let rec request () =
            let requestId = RequestId.fresh ()
            readInFlight <- Some requestId
            match options.FetchEvents with
            | Some fetch ->
                Async.StartImmediate (
                    async {
                        match! fetch lastProcessed with
                        | Ok page -> onEventsPage requestId page
                        | Error fault -> onFeedFault requestId fault
                    })
            | None ->
                Async.StartImmediate (channel.Send (EventLog (ReadEventsAfter (requestId, lastProcessed, options.PageSize))))

        and requestIfBehind () =
            if Option.isNone readInFlight && behind () then request ()

        and onEventsPage (requestId: RequestId) (page: EventPage<SessionEvent>) =
            if readInFlight = Some requestId then readInFlight <- None
            lastProcessed <- EventOffset.maxOption lastProcessed page.LastOffset
            latestKnown <- EventOffset.maxOption latestKnown page.LastOffset
            dispatch (EventsPageMsg page)
            // A non-final page means more events already exist beyond this one.
            if not page.IsEnd && Option.isNone readInFlight then request ()
            else requestIfBehind ()

        and onFeedFault (requestId: RequestId) (fault: FeedFault) =
            if readInFlight = Some requestId then readInFlight <- None
            // The feed's policy has already spent its retries by the time this is reached, so
            // do NOT re-request here: park, and re-arm on the next availability hint or
            // reconnect. The read position is untouched, so the re-arm resumes exactly where
            // consumption stopped.
            //
            // This is the seam that used to fail silently. A failed fetch became an empty
            // FINAL page, which advanced nothing; `behind ()` therefore stayed true and the
            // loop re-requested immediately — an unbounded spin, one request per round trip,
            // with no log line and nothing in the model. Drafts, title, and presence kept
            // syncing over the data channel the whole time, so the only symptom was a
            // timeline that never filled.
            dispatch (EventFeedMsg (FeedStalled (FeedFault.describe fault)))

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
            fun peerId ->
                // Enqueue under a fresh queue id (unique keys make concurrent sends safe).
                // Carry the rich body over: seed the queue entry's body root AND create the entry
                // in ONE Yjs transaction, so the send is a SINGLE doc update — exactly as the old
                // nested-body design was. Two reasons this must be atomic:
                //   1. the Session Process drains on the entry's arrival, so an entry that arrives
                //      without its body would be snapshotted as an empty durable message; and
                //   2. splitting it into multiple interleaved State frames shifts the relay timing
                //      so a `withYlmish` Set (from applying the drain's queue removal) can clobber
                //      the SENDER's just-consumed conversation/offset — a Set replaces every
                //      non-synced model field with a decode-time snapshot.
                // Body roots are `getXmlFragment` (idempotent), so writing the body inside the
                // same transaction that creates the entry is safe.
                match QueueId.create (string (System.Guid.NewGuid ())) with
                | Ok queueId ->
                    let draftBody = registry.Fragment (BodyKey.draft peerId)
                    let md = Markdown.ofFragment draftBody
                    doc.transact ((fun _ ->
                        if md <> "" then Markdown.intoFragment md (registry.Fragment (BodyKey.queued queueId))
                        dispatch (SendDraftMsg (peerId, queueId))), null)
                    // The composer empties after send: the sender's body root is a durable
                    // top-level root (it is not removed with the slot), so clear it explicitly.
                    Markdown.intoFragment "" draftBody
                | Error e -> failwithf "queue id invariant violated: %s" e
          InterruptTurn =
            fun turnId ->
                Async.StartImmediate (
                    channel.Send (Command (Request (RequestId.fresh (), InterruptAgentTurn turnId))))
          ReportPresence =
            fun focus ->
                // Presence carries the local peer's identity so collaborators can label
                // and colour the caret; the Session Process relays it to other peers.
                Async.StartImmediate (
                    channel.Send (Presence { PeerId = hello.PeerId; DisplayName = hello.DisplayName; Focus = focus })) }

    /// The session leg's lifecycle: open a transport, serve one session over it, and decide
    /// whether to come back. The outermost layer of the client, and the last one that was only
    /// reachable through a browser — its rules are extracted here so the browser supplies
    /// WebRTC and Lit, tests supply in-memory channels, and the RULES live in one place either
    /// can drive.
    module SessionLifecycle =

        /// Everything the lifecycle needs from outside itself. Four functions and no state of
        /// its own — deliberately NOT the model: reading `Connection` the instant `Serve`
        /// returns races the driver's own `DisconnectedMsg`, so the lifecycle watches the
        /// messages it routes instead of the state they will eventually produce.
        type Ports<'channel> =
            { /// Acquire a transport. Already wrapped in a resilience policy at the composition
              /// site, so an `Error` here is SETTLED — the lifecycle's job is to report it,
              /// never to try again in a little while.
              Open : unit -> Async<Result<'channel, ChannelFault>>
              /// Run one session to completion over the channel, resuming event consumption
              /// after the given offset and routing every message through the supplied
              /// dispatch. Returns when the channel closes.
              Serve : EventOffset option -> (ClientMsg -> unit) -> 'channel -> Async<unit>
              /// How far event consumption has got — where a reconnect resumes from.
              ReadPosition : unit -> EventOffset option
              /// The lifecycle's own reporting.
              Dispatch : ClientMsg -> unit }

        /// Drive the session leg to a settled state and keep it there.
        ///
        /// A channel that closes after the session was ACCEPTED is an ended session — a
        /// Process restart, a sleeping laptop, a network blip — so come back, resuming
        /// consumption from where the read position got to. A channel that closes without ever
        /// being accepted was refused (a stale token); the model holds that reason and
        /// reconnecting would only be refused again, so stop.
        ///
        /// Those two cases are the whole design, and why this cannot spin: only an accepted
        /// session earns another attempt, and every other outcome ends here.
        let run (ports: Ports<'channel>) : Async<unit> =
            let rec attempt (isFirst: bool) (resumeAfter: EventOffset option) =
                async {
                    // Announce the FIRST attempt: until a channel exists the model would read
                    // `Disconnected`, and opening one costs a handshake plus whatever retries
                    // the policy spends. A reconnect needs no announcement — `Reconnecting` is
                    // already the truer word, and the connection driver says `Connecting`
                    // itself the moment a channel is up.
                    if isFirst then ports.Dispatch ConnectingMsg
                    match! ports.Open () with
                    | Error fault -> ports.Dispatch (ConnectFailedMsg (ChannelFault.describe fault))
                    | Ok channel ->
                        // Acceptance is learned from the message that carries it, as it passes.
                        let mutable accepted = false
                        let observing msg =
                            match msg with
                            | ConnectedMsg _ -> accepted <- true
                            | _ -> ()
                            ports.Dispatch msg
                        do! ports.Serve resumeAfter observing channel
                        if accepted then return! attempt false (ports.ReadPosition ())
                }
            attempt true None
