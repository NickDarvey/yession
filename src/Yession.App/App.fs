namespace Yession.App

open Elmish
open Yjs
open Ylmish.Codec
open Yession.Domain
open Yession.Domain.Link
open Yession.Domain.Terminals
open Yession.Domain.Collab

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
          /// Send the draft in the given peer's slot: enqueue it (Phase 3). ANY co-editor may
          /// send, so the `PeerId` names whose draft it is, which is not necessarily the local
          /// peer. A pure CRDT model update under the key the slot has carried since it was
          /// published — no command round-trip; the Session Process consumes the queue and the
          /// message lands in the timeline as events.
          SendDraft : PeerId -> unit
          /// Discard a draft: empty its body, which retracts the slot through the SAME
          /// publication rule that typing published it with (`DraftSlot`). The body is what a
          /// person sees, so removing the slot without emptying it changed nothing on screen
          /// and the next keystroke re-published — the discard button appeared not to work.
          DiscardDraft : PeerId -> unit
          /// Ask the Session Process to cancel the running agent turn (Step 17). The
          /// outcome arrives as events: `AgentTurnInterrupted` on success, or nothing
          /// if the turn already finished (the request is then rejected).
          InterruptTurn : AgentTurnId -> unit
          /// Broadcast the local peer's caret+selection focus (or `None` when it leaves every
          /// collaborative field), so collaborators see the cursor. Ephemeral presence — the
          /// Session Process relays it to other peers and never persists it.
          ReportPresence : Focus option -> unit
          /// Ask the Session Process to open a terminal (Plan 13). The new terminal arrives
          /// as a `TerminalOpened` event, not as a response — one source of truth for a
          /// durable fact, and it is the one every peer already reads.
          OpenTerminal : string -> unit
          /// Ask the Session Process to close a terminal.
          CloseTerminal : TerminalId -> unit
          /// Take a terminal's stdin — enter live mode, stealing the lease if another peer
          /// holds it (Plan 13, stage 2e). The outcome arrives as a `TerminalLeaseTaken`
          /// event, on the same one-source-of-truth rule as opening a terminal.
          TakeTerminal : TerminalId -> unit
          /// Hand the terminal back to block mode. Refused unless this peer is the holder.
          ReleaseTerminal : TerminalId -> unit
          /// Type the shell instrumentation in again (Plan 13, stage 2f).
          RearmTerminal : TerminalId -> unit
          ReattachTerminal : TerminalId -> unit
          /// Keystrokes for a terminal this peer holds (Plan 14, stage 6). A FRAME, not a
          /// command: there is no response by design, because a keystroke that needed an
          /// acknowledgement would make typing a round trip. The Session Process checks the
          /// lease and drops what a non-holder sends.
          TypeIntoTerminal : TerminalId -> string -> unit
          /// The holder's viewport size, so the pty and the program inside it agree about
          /// the shape of the screen (Plan 14, stage 6).
          ResizeTerminal : TerminalId -> int -> int -> unit
          /// Send the command in a terminal composer slot: enqueue it. ANY co-editor may
          /// send, so the `PeerId` names whose slot it is. A pure CRDT write under the key
          /// the slot has carried since publication.
          SendTerminalDraft : TerminalId -> PeerId -> unit
          /// Discard a terminal composer slot by emptying its command line, which retracts
          /// the slot through the same publication rule that typing published it with.
          DiscardTerminalDraft : TerminalId -> PeerId -> unit
          /// Catch a terminal's transcript up from where this client has read to. Safe to
          /// call repeatedly: records fold by sequence number, so a re-read costs bytes and
          /// changes nothing.
          FetchTranscript : TerminalId -> unit }

    /// The platform's HTTP GET, as a TOTAL function: the body, the status it refused with,
    /// or the transport error it never got past. Totality is the whole point — the old port
    /// threw, and a thrown fetch was indistinguishable from "the log has nothing new".
    type HttpFailure =
        | HttpStatus of status: int
        | HttpUnreachable of detail: string

    /// What a GET actually returned: the bytes, and the address they came back FROM.
    ///
    /// Those are two different things now that the event log is read by cursor: the caller
    /// asks `events/after/41` and the answer arrives from `events/42-136`, which is the
    /// address whose bytes never change and therefore the only one worth keeping (Plan 20).
    /// A port that returned bytes alone made the client invent that address, which is the
    /// one thing this design exists to stop it doing.
    type HttpAnswer =
        { Url  : string
          Body : string }

    type HttpGet = string -> Async<Result<HttpAnswer, HttpFailure>>

    /// A client's own copy of the answers it has been given. The browser backs this with the
    /// Cache API — whole responses, keyed by the address they came from, enumerable and
    /// persistable — but nothing above this type knows that.
    ///
    /// Total, like the feed: a store that cannot be opened reads empty, and the client is
    /// exactly the client it is today, asking the network from its cursor.
    ///
    /// The port exists so the store can be swapped, and there is exactly one thing that may
    /// trigger a swap: measurement that the Cache API is not holding what a client was given.
    /// Then an IndexedDB store keyed by offset goes behind this same port and the Cache API one
    /// is deleted — a replacement, never an addition beside it. An insecure context is
    /// explicitly not a trigger: a store only that deployment exercises is the spare that rots
    /// unverified, and its real remedy is one flag on the operator's side.
    [<RequireQualifiedAccess>]
    type HistoryCache =
        { /// Every address kept, in NO promised order — a bag, not a queue. The Cache API's
          /// `put` of an address already held deletes and re-appends it, so an answer two tabs
          /// both fetched moves to the end of the enumeration long after it was first kept.
          /// The replay orders by what the answers hold; nothing may order by this.
          Stored : unit -> Async<string list>
          /// One kept answer's body, or `None` when it is no longer there.
          Read   : string -> Async<string option>
          /// Keep this answer under the address it came back from.
          Write  : string -> string -> Async<unit> }

    module HistoryCache =

        /// The client that keeps nothing: every replay is empty and every write is dropped.
        /// What an insecure context gets (`window.caches` needs one), and what every
        /// non-browser peer gets, without either having to care.
        let none : HistoryCache =
            { Stored = fun () -> async.Return []
              Read = fun _ -> async.Return None
              Write = fun _ _ -> async.Return () }

    /// One terminal's kept answers (Plan 22). `HistoryCache` with one field more, and that
    /// field is the whole difference between the two feeds: an event envelope carries its own
    /// offset, so the event store never has to be told where an answer starts. A transcript
    /// line cannot — the file is an asciicast and a private index field in it would stop it
    /// being one — so the seq is kept BESIDE the bytes, taken from what the client asked for
    /// rather than parsed back out of the address it was given.
    [<RequireQualifiedAccess>]
    type TranscriptCache =
        { /// Every address kept, in NO promised order — see `HistoryCache.Stored`. The line an
          /// answer starts on is what a walk goes by, and it rides beside the bytes.
          Stored : unit -> Async<string list>
          /// One kept answer: the line its first line is, and the bytes.
          Read   : string -> Async<(int * string) option>
          /// Keep this answer under the address it came back from, at the line it starts on.
          Write  : string -> int -> string -> Async<unit> }

    /// Every terminal's store, one per terminal.
    ///
    /// Not one store with everything in it, and the reason is the replay. A walk orders one
    /// terminal's answers by the line each starts on, which says nothing about WHOSE they are:
    /// two terminals' answers in one store would interleave, and telling them apart means
    /// parsing an address — the one thing this client is never allowed to read meaning out of.
    /// The store's own name answers it instead, before the walk starts.
    type TranscriptCaches =
        { /// The store for one terminal.
          For  : TerminalId -> Async<TranscriptCache>
          /// Every terminal this client has kept anything for, read from the stores' own
          /// names — never from the model, which this same startup path has only just filled.
          Kept : unit -> Async<TerminalId list> }

    module TranscriptCache =

        let none : TranscriptCache =
            { Stored = fun () -> async.Return []
              Read = fun _ -> async.Return None
              Write = fun _ _ _ -> async.Return () }

    module TranscriptCaches =

        /// The client that keeps nothing: every replay is empty and every write is dropped.
        let none : TranscriptCaches =
            { For = fun _ -> async.Return TranscriptCache.none
              Kept = fun () -> async.Return [] }

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

    /// Reading a terminal's transcript over HTTP — the history leg of the terminal feed
    /// (Plan 13), read by cursor since Plan 22 and a deliberate copy of `EventFetch`'s shape
    /// because it is the same problem: a position in, an answer whose bounds never move back,
    /// and a store the client keeps it in.
    ///
    /// One difference from the event feed, and it is the reason this is a separate module
    /// rather than a reuse: a transcript's first line is its asciicast header, not a
    /// record. The first answer therefore yields one fewer record than it has lines, and
    /// every record's sequence number is its LINE index — which is what keeps a fetched
    /// answer and a live frame talking about the same thing.
    module TranscriptFetch =

        /// One fetched page: the records it carried with their sequence numbers, and
        /// whether the transcript continues past it.
        type TranscriptPage =
            { Records : (int * TranscriptRecord) list
              /// The transcript's own header, when this page carried it (line 0, so the
              /// answer from a client with nothing yet, and no other). Kept rather than
              /// discarded because the replay view (Plan 13, stage 3e) rebuilds a `.cast`
              /// from these records, and a `.cast` without its header is not one — the
              /// recorded width and height are what make a replay come out the shape the
              /// terminal actually was.
              Header : TranscriptHeader option
              /// One past the last line this page covered.
              NextSeq : int
              /// A capped answer means the server had more to give; anything shorter — a
              /// `204`, which arrives here as no lines at all — is the transcript's tail.
              IsEnd : bool }

        type TranscriptFeed = TerminalId -> int -> Async<Result<TranscriptPage, FeedFault>>

        /// Build a feed over the platform's HTTP GET, resolving routes with `urlOf` (the
        /// browser passes `SessionRoute.relative` and lets `<base href>` do the rest).
        /// The lines of one answer, numbered from `first` — the seq of its first line.
        ///
        /// ONE decoder, used by the network read and by the replay of what was kept, for the
        /// reason `EventFetch.decodeLines` is one: what is kept IS what the network returned,
        /// verbatim, and a store holding anything else would be a second wire format.
        ///
        /// A line that will not decode fails the whole answer: a partially decoded transcript
        /// is not history, it is a guess.
        let decodeLines (first: int) (body: string) : Result<TranscriptPage, FeedFault> =
            let lines = body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
            let rec decode i acc header =
                if i >= lines.Length then Ok (List.rev acc, header)
                else
                    match Codec.fromString Codec.transcriptLine lines.[i] with
                    | Ok (TranscriptRecordLine record) -> decode (i + 1) ((first + i, record) :: acc) header
                    // The header is line 0 and carries no output, so it is no record — but
                    // it is KEPT, because a replay needs it.
                    | Ok (TranscriptHeaderLine h) -> decode (i + 1) acc (Some h)
                    | Error e -> Error (FeedCorrupt e)
            match decode 0 [] None with
            | Error fault -> Error fault
            | Ok (records, header) ->
                Ok
                    { Records = records
                      Header = header
                      NextSeq = first + lines.Length
                      IsEnd = lines.Length < TranscriptChunk.size }

        /// Build a feed over the platform's HTTP GET, resolving routes with `urlOf` (the
        /// browser passes `SessionRoute.relative` and lets `<base href>` do the rest).
        ///
        /// The cursor, and nothing else: the line this client has folded through, sent as-is.
        /// The server picks the bounds of the answer and redirects to them, so no arithmetic
        /// here can address the wrong lines — there is none to get wrong.
        ///
        /// The ONE number this computes is where the answer starts, and it comes from what it
        /// ASKED rather than from where the answer lives: the answer to `after n` begins at
        /// line `n + 1`. A transcript line cannot carry its own index — the file is an
        /// asciicast and a private index field in it would stop it being one — so the cursor
        /// contract is what numbering rests on, and it has a test of its own (Plan 22).
        /// `caches` is where a settled answer is kept — `TranscriptCaches.none` for a client
        /// with no store, which is then exactly today's client, asking the network from its
        /// cursor.
        ///
        /// Keeping happens HERE rather than in a wrapper around the GET, as the event feed's
        /// does, because the two things that have to be written down together are known in
        /// two different places: the address comes back with the answer, and the line it
        /// starts on is what this call asked for. A wrapper around the GET can see only one
        /// of them.
        let overHttp
            (caches: TranscriptCaches)
            (get: HttpGet)
            (urlOf: SessionRoute -> string)
            (token: string option)
            : TranscriptFeed =
            fun terminal fromSeq ->
                async {
                    let first = max 0 fromSeq
                    let after = if first = 0 then None else Some (first - 1)
                    let tokenSuffix =
                        token
                        |> Option.map (fun t -> sprintf "?token=%s" (System.Uri.EscapeDataString t))
                        |> Option.defaultValue ""
                    let url = urlOf (TerminalTranscriptAfter (TerminalId.value terminal, after)) + tokenSuffix
                    match! get url with
                    | Error (HttpUnreachable detail) -> return Error (FeedUnreachable detail)
                    | Error (HttpStatus status) -> return Error (FeedRefused status)
                    | Ok answer ->
                        match decodeLines first answer.Body with
                        | Error fault -> return Error fault
                        | Ok page ->
                            // An answer with no lines is the `204` — the caller is current.
                            // There is nothing to keep, and keeping it would put an empty
                            // entry in the store under the CURSOR's address (no redirect
                            // happened, so that is where this came back from), which the
                            // replay would then read as a hole in the recording.
                            if answer.Body.Trim().Length > 0 then
                                let! cache = caches.For terminal
                                do! cache.Write answer.Url first answer.Body
                            return Ok page
                }

        /// Fold what this client has already been given for every terminal it kept anything
        /// for, oldest first, before anything asks the network.
        ///
        /// Runs AFTER the event replay, never beside it: a terminal exists because an event
        /// said so, and records folded before that event has been folded have nowhere to
        /// land.
        ///
        /// **The order comes from the answers, never from the store** — the event replay's
        /// rule, for the same reason: a Cache API `put` of an address already kept deletes and
        /// re-appends it, so two tabs fetching one range moves the earliest answer to the end
        /// and a walk that trusted the store's order would call everything before it a hole.
        /// Here the line an answer starts on rides beside the bytes already (`TranscriptCache`),
        /// so the ordering costs nothing but the sort.
        ///
        /// **A gap stops that terminal's walk, and is noticed in the SEQUENCE rather than in
        /// the addresses.** An entry evicted from the middle leaves the rest in the store, so
        /// the walk reaches an answer whose first line is not one past the last folded. The
        /// walk stops there because `ReadThrough` is both "shown through" and "resume from",
        /// and advancing it over unread lines would skip them for ever — but only for THAT
        /// terminal, which is the other thing one store per terminal buys.
        let replay (caches: TranscriptCaches) (dispatch: ClientMsg -> unit) : Async<unit> =
            async {
                let! terminals = caches.Kept ()
                for terminal in terminals do
                    let! cache = caches.For terminal
                    let! stored = cache.Stored ()
                    // What this terminal kept, in transcript order. An address the store listed
                    // and then could not answer simply is not here, and the walk below notices
                    // it as the hole it is.
                    let kept = ResizeArray<int * string> ()
                    for url in stored do
                        match! cache.Read url with
                        | None -> ()
                        | Some answer -> kept.Add answer
                    let ordered = kept |> Seq.sortBy fst |> List.ofSeq
                    // One past the last line folded. `0` before anything, because line 0 is
                    // the header and a first answer always carries it.
                    let mutable expected = 0
                    let mutable stop = false
                    for (first, body) in ordered do
                        if not stop then
                            if first > expected then stop <- true
                            else
                                match decodeLines first body with
                                // A recording that will not decode is not history, it is
                                // a guess — and unlike the event log there is no feed
                                // health to report it on, because a terminal's history
                                // failing costs that terminal and nothing else.
                                | Error _ -> stop <- true
                                | Ok page ->
                                    page.Header
                                    |> Option.iter (fun h -> dispatch (TerminalHeaderMsg (terminal, h)))
                                    for (seq, record) in page.Records do
                                        dispatch (TerminalRecordMsg (terminal, seq, record))
                                    dispatch (TerminalReadThroughMsg (terminal, page.NextSeq))
                                    expected <- max expected page.NextSeq
            }

    /// How a connection consumes the event log (Step 07).
    type ConnectOptions =
        { /// Resume consumption after this offset (the model's `LastProcessedOffset`
          /// when reconnecting); `None` reads from the beginning.
          ResumeAfter : EventOffset option
          /// Events per `ReadEventsAfter` request.
          PageSize : int
          /// When given, event pages are fetched through this feed instead of
          /// `ReadEventsAfter` frames — the browser passes its cursor-addressed HTTP
          /// fetcher here, and serves history out of its OWN store of the ranges it was
          /// given (the HTTP cache keeps none of it; every response is `no-store`); pure
          /// data-channel peers leave it `None` and read over frames.
          FetchEvents : EventFeed option
          /// When given, terminal history is fetched through this feed (Plan 13). `None`
          /// leaves a client with only what arrives live — which is correct for a peer
          /// that has no HTTP leg, and is why this is optional rather than required.
          FetchTranscripts : TranscriptFetch.TranscriptFeed option
          /// What to do with a terminal's screen when the Session Process sends one (Plan
          /// 14, stage 6). The browser seeds its emulator; a client with none — a headless
          /// peer, a test — ignores it and loses nothing, because the transcript is the
          /// record and this is only the view.
          OnTerminalSnapshot : TerminalId -> TranscriptKeyframe -> unit
          /// How far the MODEL has consumed the log. When given, this — not the read
          /// loop's own bookkeeping — is what "how far have we got" means.
          ///
          /// One source of truth, because two drift. The loop's private cursor advances
          /// when a page ARRIVES; the model's advances when the page is FOLDED. Anything
          /// that discards a fold — a decode failure keeping the current model, a
          /// reconnect onto a restored replica — moves them apart, and a loop reading its
          /// own cursor then believes it is up to date while the model is missing events
          /// nothing will ever offer again.
          ///
          /// Asking the model instead makes that unrepresentable: a model that lost a
          /// fold is visibly behind, so the next hint re-reads it. The fold is
          /// offset-gated, so a re-read costs a round trip and changes nothing else.
          ReadPosition : (unit -> EventOffset option) option
          /// How far the MODEL has read one terminal's transcript, for the same reason and on
          /// the same terms as `ReadPosition` above. Without it, a client that replayed a
          /// terminal out of its own store would start its first read at line 0 and fetch
          /// every line it already has.
          TranscriptReadPosition : (TerminalId -> int) option }

    module ConnectOptions =
        let defaults : ConnectOptions =
            { ResumeAfter = None
              PageSize = 100
              FetchEvents = None
              FetchTranscripts = None
              OnTerminalSnapshot = fun _ _ -> ()
              ReadPosition = None
              TranscriptReadPosition = None }

    /// The HTTP event feed for `ConnectOptions.FetchEvents`: sends "events after offset X"
    /// as exactly that — a cursor (`/events/after/{n}`) — and decodes the JSONL envelopes
    /// of whatever the server redirects it to. The address it lands on names its own
    /// bounds and so never changes its bytes, which is what lets a client keep the answer
    /// (Plan 20); this function does not keep anything itself. Also home to the
    /// shipped resilience policy for that feed — the policy is a value here so the
    /// composition site is one line and the TEST runs the same policy that ships.
    module EventFetch =

        /// The envelope lines of one answer, or the first one that would not decode.
        ///
        /// ONE decoder, used by the network read and by the replay of what was kept, because
        /// what is kept IS what the network returned — verbatim. A store holding anything
        /// else would be a second wire format only this client can write and only this client
        /// can read.
        ///
        /// A line that will not decode fails the whole answer: a partially decoded page is
        /// not history, it is a guess.
        let decodeLines (body: string) : Result<EventEnvelope<SessionEvent> list, FeedFault> =
            let lines = body.Split '\n' |> Array.filter (fun l -> l.Trim().Length > 0)
            let rec decode remaining acc =
                match remaining with
                | [] -> Ok (List.rev acc)
                | line :: rest ->
                    match Codec.fromString Codec.sessionEventEnvelope line with
                    | Ok envelope -> decode rest (envelope :: acc)
                    | Error e -> Error (FeedCorrupt e)
            decode (List.ofArray lines) []

        /// Keep every settled answer under the address it came back from.
        ///
        /// Composed around the GET rather than around the feed, because by the time an answer
        /// is an `EventPage` the address it arrived from is gone — and that address, which the
        /// server chose and whose bounds never move, is the only thing worth keying by. Only
        /// successes are kept, so a retried fetch stores once.
        let storing (cache: HistoryCache) (get: HttpGet) : HttpGet =
            fun url ->
                async {
                    match! get url with
                    | Ok answer ->
                        do! cache.Write answer.Url answer.Body
                        return Ok answer
                    | Error failure -> return Error failure
                }

        /// Build a feed over the platform's HTTP GET. `urlOf` resolves a route for this
        /// platform: a browser passes `SessionRoute.relative` and lets `<base href>` do the
        /// rest, a Node client prefixes the session's absolute address. `token` = a
        /// session-minted peer token appended as `?token=` — the cookie-less path (Node
        /// clients); the browser passes None and rides its same-origin auth cookie.
        ///
        /// Every failure is a `FeedFault`, never a fabricated page and never an exception:
        /// a transport error is `FeedUnreachable`, a refusal keeps its status (so 401 and
        /// 503 can be told apart), and a line that will not decode is `FeedCorrupt`. This
        /// function does not retry — `Resilience.Policy.guard` does, wrapped around it.
        let overHttp (get: HttpGet) (urlOf: SessionRoute -> string) (token: string option) : EventFeed =
            fun after ->
                async {
                    let tokenSuffix =
                        token
                        |> Option.map (fun t -> sprintf "?token=%s" (System.Uri.EscapeDataString t))
                        |> Option.defaultValue ""
                    // The cursor, and nothing else: the offset this client has folded
                    // through, sent as-is. The server picks the bounds of the answer and
                    // redirects to them, so no arithmetic here can address the wrong
                    // events — there is none to get wrong.
                    let url = urlOf (EventsAfter after) + tokenSuffix
                    match! get url with
                    | Error (HttpUnreachable detail) -> return Error (FeedUnreachable detail)
                    | Error (HttpStatus status) -> return Error (FeedRefused status)
                    | Ok answer ->
                        match decodeLines answer.Body with
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
                                      // A capped answer means the server had more to give;
                                      // anything shorter — a `204` included, which arrives
                                      // here as no lines at all — is the log's current tail.
                                      IsEnd = List.length envelopes < EventChunk.size }
                }

        /// Fold what this client has already been given, oldest first, before anything asks
        /// the network — and before the client even knows whether it may connect.
        ///
        /// This is not part of the read loop and must not become part of it: that loop chases
        /// a moving remote end, and a replay has no remote end to chase. It is a pure drain of
        /// what is already here, which is why it can run with no transport, no session, and no
        /// answer from `/me`.
        ///
        /// **The order comes from the events, never from the store.** `Stored` answers in
        /// whatever order the store happens to hold, and that order is not ascending: the
        /// Cache API's `put` of an address already kept DELETES the entry and appends the new
        /// one, so the first time two tabs of one session fetch the same range — both at the
        /// same cursor, one storing what the other already had — the earliest answer moves to
        /// the END. A walk that trusted the store's order then read a later answer first and
        /// called every event before it missing, on a client whose store held them all. So the
        /// answers are read, asked where they START, and walked in that order.
        ///
        /// **A gap stops the fold, and is noticed in the events rather than in the
        /// addresses.** An entry evicted from the middle leaves the rest in the store, so the
        /// walk reaches an answer whose first offset is not one past the last folded. That
        /// discontinuity is the detection; nothing parses an address to find it. The fold stops
        /// there because `LastProcessedOffset` is both "shown through" and "resume from", and
        /// advancing it over unread offsets would skip them for ever — but the gap is
        /// REPORTED, and the cursor is left sitting exactly where the fill has to start, so the
        /// next ordinary request repairs it.
        ///
        /// A gap is reported as what it is — history this DEVICE does not hold — and never as
        /// the feed's health. Nothing has been read from the network at this point, so a
        /// `FeedStalled` here says the read loop gave up before it had started, which is how a
        /// cold open flashed a red "history paused" at everyone whose store was out of order.
        let replay (cache: HistoryCache) (dispatch: ClientMsg -> unit) : Async<unit> =
            async {
                let! stored = cache.Stored ()
                // What each kept answer holds, in log order. An address the store listed and
                // then could not answer, an empty body, and a body that will not decode are
                // all the same thing here — an answer this walk does not have — and each
                // leaves a hole the offsets below notice, rather than a special case up here.
                let kept = ResizeArray<int64 * EventEnvelope<SessionEvent> list> ()
                for url in stored do
                    match! cache.Read url with
                    | None -> ()
                    | Some body ->
                        match decodeLines body with
                        | Error _ | Ok [] -> ()
                        | Ok envelopes -> kept.Add (EventOffset.value (List.head envelopes).Offset, envelopes)
                let ordered = kept |> Seq.sortBy fst |> List.ofSeq
                let mutable folded : EventOffset option = None
                let mutable stop = false
                for (first, envelopes) in ordered do
                    if not stop then
                        let expected =
                            match folded with
                            | Some o -> EventOffset.value o + 1L
                            | None -> 0L
                        if first > expected then
                            // Where the kept history resumes. Everything between the cursor —
                            // which is `expected - 1`, exactly where the fill has to start —
                            // and this offset is not on this device.
                            dispatch (LocalHistoryGapMsg (List.head envelopes).Offset)
                            stop <- true
                        else
                            dispatch (
                                LocalHistoryMsg
                                    { Events = envelopes
                                      LastOffset =
                                        envelopes |> List.tryLast |> Option.map (fun e -> e.Offset)
                                      // The end of what is KEPT, never a claim about the
                                      // log: the cursor decides that, and it has not
                                      // been asked yet.
                                      IsEnd = true })
                            folded <- EventOffset.maxOption folded (Some (List.last envelopes).Offset)
                // Looked. Said even when nothing was kept and even when a gap cut the walk
                // short: what the timeline needs to know is whether this client has been to
                // look, not whether looking found anything.
                dispatch HistoryReadMsg
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
        (texts: TextRegistry)
        (hello: PeerHelloPayload)
        (dispatch: ClientMsg -> unit)
        (channel: FrameChannel<string>)
        : Connection =
        // Registered here and released by `Run`, which is why the two are one verb rather
        // than a disposer for the caller to remember: this listener belongs to the CHANNEL,
        // not to the doc, and the doc outlives every channel that ever spoke over it. A
        // client that reconnects — routine now that the link notices its own death — would
        // otherwise leave one behind per attempt, each still sending into a shut channel,
        // and every local update would walk all of them.
        let stopSending =
            DocSync.onLocalUpdate doc (fun payload ->
                Async.StartImmediate (channel.Send (State (StateSync payload))))

        // No commands are issued by the client anymore (sending is a CRDT write);
        // responses to any future commands are currently uncorrelated.
        let onResponse (_requestId: RequestId) (_result: SessionCommandResult) = ()

        // The consumption loop's own read position (seeded for reconnect catch-up).
        let mutable lastProcessed : EventOffset option = options.ResumeAfter
        let mutable latestKnown : EventOffset option = None
        let mutable readInFlight : RequestId option = None

        // Where consumption has actually got to. The model's answer when the composition
        // gave one; the loop's own bookkeeping otherwise (a peer with no model behind it).
        let readCursor () =
            match options.ReadPosition with
            | Some position -> position ()
            | None -> lastProcessed

        let behind () =
            match latestKnown, readCursor () with
            | Some latest, Some processed -> EventOffset.value latest > EventOffset.value processed
            | Some _, None -> true
            | None, _ -> false

        // `request` delivers fetched pages back through `onEventsPage`, which may in
        // turn request the next page — hence the mutual recursion.
        let rec request () =
            let requestId = RequestId.fresh ()
            readInFlight <- Some requestId
            let after = readCursor ()
            match options.FetchEvents with
            | Some fetch ->
                Async.StartImmediate (
                    async {
                        match! fetch after with
                        | Ok page -> onEventsPage requestId page
                        | Error fault -> onFeedFault requestId fault
                    })
            | None ->
                Async.StartImmediate (channel.Send (EventLog (ReadEventsAfter (requestId, after, options.PageSize))))

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

        // How far a contiguous HTTP read has got through each terminal's transcript. Held
        // here rather than read back off the model for the same reason `lastProcessed` is:
        // the read loop must not depend on a model update having been folded yet.
        //
        // Only HTTP reads advance it. A live record at a higher seq is still folded into
        // the model — it is keyed by seq, so it lands wherever it belongs — but it does not
        // prove the records BEFORE it have arrived, and treating it as if it did is how a
        // client ends up with a hole it will never fetch.
        let transcriptRead = System.Collections.Generic.Dictionary<string, int> ()

        // Where this terminal's transcript has actually been read to. The model's answer when
        // the composition gave one; the loop's own bookkeeping otherwise (a peer with no model
        // behind it) — the same two sources, for the same reason, as the event cursor above.
        let readPositionOf (terminal: TerminalId) =
            match options.TranscriptReadPosition with
            | Some position -> position terminal
            | None ->
                match transcriptRead.TryGetValue (TerminalId.value terminal) with
                | true, seq -> seq
                | _ -> 0

        let fetchTranscript (terminal: TerminalId) =
            match options.FetchTranscripts with
            | None -> ()
            | Some fetch ->
                let rec readFrom (fromSeq: int) =
                    async {
                        match! fetch terminal fromSeq with
                        | Error _ ->
                            // A transcript read that fails is not a session that failed: the
                            // live leg keeps delivering, and the next availability hint
                            // re-arms this. Parking beats spinning.
                            return ()
                        | Ok page ->
                            for (seq, record) in page.Records do
                                dispatch (TerminalRecordMsg (terminal, seq, record))
                            transcriptRead.[TerminalId.value terminal] <- max (readPositionOf terminal) page.NextSeq
                            page.Header |> Option.iter (fun h -> dispatch (TerminalHeaderMsg (terminal, h)))
                            dispatch (TerminalReadThroughMsg (terminal, page.NextSeq))
                            // `NextSeq > fromSeq` guards the one way this could spin: a
                            // chunk that yields nothing new would otherwise be re-read for
                            // ever at the same offset.
                            if not page.IsEnd && page.NextSeq > fromSeq then return! readFrom page.NextSeq
                    }
                Async.StartImmediate (readFrom (readPositionOf terminal))

        let dispatchAndConsume (msg: ClientMsg) =
            dispatch msg
            match msg with
            | TerminalAvailableMsg (terminal, length) ->
                // The terminal-feed counterpart of `EventsAvailable`: a hint that there is
                // more, answered by a read rather than by trusting the hint's contents.
                if length > readPositionOf terminal then fetchTranscript terminal
            | TerminalRecordMsg (terminal, seq, _) when seq > readPositionOf terminal ->
                // Live output beyond the read position means history exists that this
                // client has not fetched — the records between where it read to and where
                // the live stream now is. Ask for them.
                fetchTranscript terminal
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
            async {
                try
                    do!
                        Connection.run hello dispatchAndConsume (DocSync.applyRemote doc) onResponse onEventsPage options.OnTerminalSnapshot channel
                // However the pump ends — the channel closing, a fault, cancellation — the
                // doc stops feeding it.
                finally stopSending ()
            }
          SendDraft =
            fun peerId ->
                // Enqueue under the key the draft has carried since it was published, read from
                // the slot rather than minted here: that is what makes two co-editors sending at
                // once ONE queue entry instead of the same message twice.
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
                match SyncedStateSync.draftQueueId doc peerId with
                | Some queueId ->
                    let draftBody = registry.Fragment (BodyKey.draft peerId)
                    let md = Markdown.ofFragment draftBody
                    doc.transact ((fun _ ->
                        if md <> "" then Markdown.intoFragment md (registry.Fragment (BodyKey.queued queueId))
                        dispatch (SendDraftMsg peerId)
                        // The composer empties in the SAME transaction: the body root is durable
                        // (it is not removed with the slot), so it must be cleared explicitly —
                        // and clearing it separately would publish an update where the body still
                        // has content but the slot is gone, which `DraftSlot` answers by
                        // republishing the very slot the send just removed.
                        Markdown.intoFragment "" draftBody), null)
                // No slot means nothing published to send: an untouched composer, or a draft a
                // co-editor sent a moment ago. Both are no-ops, not errors.
                | None -> ()
          DiscardDraft =
            fun peerId ->
                // Emptying the body IS the discard: `DraftSlot.follow` watches that fragment and
                // retracts the slot the moment it goes empty, exactly as it published on the
                // first keystroke. The slot removal is dispatched in the SAME transaction so the
                // send-shaped invariant holds here too — one doc update, never a moment where the
                // body is empty but the slot still advertises a draft to collaborators.
                let body = registry.Fragment (BodyKey.draft peerId)
                doc.transact ((fun _ ->
                    Markdown.intoFragment "" body
                    dispatch (DiscardDraftMsg peerId)), null)
          InterruptTurn =
            fun turnId ->
                Async.StartImmediate (
                    channel.Send (Command (Request (RequestId.fresh (), InterruptAgentTurn turnId))))
          ReportPresence =
            fun focus ->
                // Presence carries the local peer's identity so collaborators can label
                // and colour the caret; the Session Process relays it to other peers.
                Async.StartImmediate (
                    channel.Send (Presence { PeerId = hello.PeerId; DisplayName = hello.DisplayName; Focus = focus }))
          OpenTerminal =
            fun title ->
                Async.StartImmediate (channel.Send (Command (Request (RequestId.fresh (), OpenTerminal title))))
          CloseTerminal =
            fun terminalId ->
                Async.StartImmediate (channel.Send (Command (Request (RequestId.fresh (), CloseTerminal terminalId))))
          TakeTerminal =
            fun terminalId ->
                Async.StartImmediate (channel.Send (Command (Request (RequestId.fresh (), TakeTerminalLease terminalId))))
          ReleaseTerminal =
            fun terminalId ->
                Async.StartImmediate (
                    channel.Send (Command (Request (RequestId.fresh (), ReleaseTerminalLease terminalId))))
          RearmTerminal =
            fun terminalId ->
                Async.StartImmediate (channel.Send (Command (Request (RequestId.fresh (), RearmTerminal terminalId))))
          ReattachTerminal =
            fun terminalId ->
                Async.StartImmediate (channel.Send (Command (Request (RequestId.fresh (), ReattachTerminal terminalId))))
          TypeIntoTerminal =
            fun terminalId data ->
                Async.StartImmediate (channel.Send (Terminal (TerminalInput (terminalId, data))))
          ResizeTerminal =
            fun terminalId cols rows ->
                Async.StartImmediate (channel.Send (Terminal (TerminalResize (terminalId, cols, rows))))
          SendTerminalDraft =
            fun terminal author ->
                // Enqueue under the key the slot has carried since publication — read from
                // the doc, never minted here, so two co-editors sending at once produce ONE
                // entry rather than running the command twice.
                //
                // Command text copied over, the model updated, and the composer emptied all
                // in ONE transaction, for the same two reasons the message send is atomic:
                // the Session Process drains on the entry's arrival (an entry without its
                // command would run as an empty one), and a split update lets a `withYlmish`
                // Set from the drain's removal clobber non-synced model fields.
                match SyncedStateSync.terminalDraftQueueId doc terminal author with
                | Some queueId ->
                    TerminalText.moveInto
                        doc
                        texts
                        (BodyKey.terminalDraft terminal author)
                        (BodyKey.terminalQueued queueId)
                        (fun () -> dispatch (SendTerminalDraftMsg (terminal, author)))
                // No slot means nothing published to send: an untouched composer, or one a
                // co-editor sent a moment ago. Both are no-ops.
                | None -> ()
          DiscardTerminalDraft =
            fun terminal author ->
                // Emptying the command line IS the discard — the publication rule retracts
                // the slot the moment it goes empty, exactly as typing published it.
                doc.transact ((fun _ ->
                    TerminalText.clear texts (BodyKey.terminalDraft terminal author)
                    dispatch (DiscardTerminalDraftMsg (terminal, author))), null)
          FetchTranscript = fetchTranscript }

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
              /// Wait before the next attempt, and say whether to make one.
              ///
              /// `Some delay` is the supervised schedule. `None` is "wait to be asked" — the
              /// park a REFUSED peer gets, because no schedule can help it: the same token
              /// would be refused again, and only a person (a fresh login, a button) can
              /// change the answer.
              ///
              /// Returning early is how a trigger shortens a wait — the network came back,
              /// someone pressed retry — WITHOUT becoming a second schedule. `false` ends the
              /// lifecycle, which is how a page that is going away stops it.
              WaitBeforeRetry : System.TimeSpan option -> Async<bool>
              /// The lifecycle's own reporting.
              Dispatch : ClientMsg -> unit }

        /// How long to wait before trying to open a transport again, once the policy at the
        /// composition site has spent its own budget on the attempt that just failed.
        ///
        /// It never runs out, and that is the point: a session that is not there YET may still
        /// come back — a laptop closed on a train, a Process restarting, a tunnel dropping —
        /// and the client that gave up after four seconds made "reload the tab" the only cure.
        /// The one outcome that earns no further attempt is a refusal, and it parks rather
        /// than backing off, because waiting longer cannot fix a token.
        ///
        /// Jittered for the reason the feed's policy is: a Process restart drops every peer at
        /// the same instant, and an unjittered backoff brings them all back in lockstep.
        let supervision (random: unit -> float) : Resilience.Schedule =
            Resilience.Schedule.exponential
                (System.TimeSpan.FromSeconds 1.0)
                2.0
                (System.TimeSpan.FromMinutes 1.0)
                System.Int32.MaxValue
            |> Resilience.Schedule.jittered 0.5 random

        /// Drive the session leg to a settled state and keep it there.
        ///
        /// Three outcomes, and each earns a different next move:
        ///
        /// * **Accepted, then the channel closed** — an ended session (a Process restart, a
        ///   sleeping laptop, a network blip). Come straight back, resuming consumption from
        ///   where the read position got to.
        /// * **Never opened** — the session is not reachable. Keep trying on `supervision`,
        ///   which never runs out. The policy at the composition site has already spent its
        ///   budget on the attempt that just failed; this is the slower outer loop, and it is
        ///   not a retry loop wrapped around a retry loop — it is the difference between "this
        ///   attempt failed" and "give up on the session", which are not the same claim.
        /// * **Opened but refused** — a stale token. No schedule can help, because the same
        ///   token would be refused again, so PARK: wait to be asked, and let a person supply
        ///   the one thing that can change the answer.
        ///
        /// That is also why this cannot spin. Every path either succeeds, backs off, or waits
        /// for a human, and the only unbounded one is bounded by a minute.
        let run (supervision: Resilience.Schedule) (ports: Ports<'channel>) : Async<unit> =
            let rec attempt (announce: bool) (failures: int) (resumeAfter: EventOffset option) =
                async {
                    // Announce an attempt that follows a wait: until a channel exists the model
                    // would read `Disconnected`, and opening one costs a handshake plus whatever
                    // retries the policy spends. A reconnect after an ACCEPTED session needs no
                    // announcement — `Reconnecting` is already the truer word, and the driver
                    // says `Connecting` itself the moment a channel is up.
                    if announce then ports.Dispatch ConnectingMsg
                    match! ports.Open () with
                    | Error fault ->
                        ports.Dispatch (ConnectFailedMsg (ChannelFault.describe fault))
                        let attempts = failures + 1
                        match! ports.WaitBeforeRetry (supervision attempts) with
                        | true -> return! attempt true attempts resumeAfter
                        | false -> return ()
                    | Ok channel ->
                        // Acceptance is learned from the message that carries it, as it passes.
                        let mutable accepted = false
                        let observing msg =
                            match msg with
                            | ConnectedMsg _ -> accepted <- true
                            | _ -> ()
                            ports.Dispatch msg
                        do! ports.Serve resumeAfter observing channel
                        if accepted then
                            // A session that worked resets the backoff: the next failure is
                            // this client's first, not the continuation of an old streak.
                            return! attempt false 0 (ports.ReadPosition ())
                        else
                            match! ports.WaitBeforeRetry None with
                            | true -> return! attempt true 0 resumeAfter
                            | false -> return ()
                }
            attempt true 0 None
