namespace Yession.Domain.Collab

open Yession.Domain
open Yession.Domain.Terminals

// The Ylmish sync boundary (Step 05, extended by Phase 3's message queue).
// `SyncedSessionState` is the only state that crosses it: the codec below names exactly
// the fields that sync and how each merges (docs/design.md §1 "Ylmish is the sync
// boundary"). The conversation projection is deliberately never mentioned, so it can
// never enter the Yjs document. `DocSync` is the transport adapter that moves Yjs
// updates over the opaque `State` frame.

open FSharp.Data.Adaptive
open Ylmish
open Ylmish.Codec

/// The adaptive companion of `SyncedSessionState`, hand-written in place of Adaptify
/// codegen (the model is small and Adaptify would add a build step). Drafts and queue
/// entries are keyed by their app-minted ids so concurrent — even offline — creation is
/// safe: different keys never conflict.
[<RequireQualifiedAccess>]
type AdaptiveSyncedState =
    { Drafts : cmap<string, DraftState>
      Queue : cmap<string, QueuedMessage>
      Title : cval<Text>
      SharedBrief : cval<SharedBrief option>
      TerminalDrafts : cmap<string, TerminalDraft>
      Pending : cmap<string, PendingAct>
      Model : cval<ModelId option> }

module SyncedStateSync =

    /// The doc key of a terminal composer slot. A composite because the slot is keyed by a
    /// PAIR in the model and by a string in the doc; `:` is safe as the separator because a
    /// `TerminalId` is Crockford base32 and can never contain one, so the split is
    /// unambiguous from the left.
    module TerminalDraftKey =

        let make (terminal: TerminalId) (author: PeerId) : string =
            TerminalId.value terminal + ":" + PeerId.value author

        let parse (key: string) : (TerminalId * PeerId) option =
            let idx = key.IndexOf ':'
            if idx <= 0 then None
            else
                match TerminalId.create (key.Substring (0, idx)), PeerId.create (key.Substring (idx + 1)) with
                | Ok terminal, Ok author -> Some (terminal, author)
                | _ -> None

    let private draftsByKey (m: SyncedSessionState) : HashMap<string, DraftState> =
        m.Drafts |> Map.toSeq |> Seq.map (fun (k, v) -> PeerId.value k, v) |> HashMap.ofSeq

    let private queueByKey (m: SyncedSessionState) : HashMap<string, QueuedMessage> =
        m.Queue |> Map.toSeq |> Seq.map (fun (k, v) -> QueueId.value k, v) |> HashMap.ofSeq

    let private terminalDraftsByKey (m: SyncedSessionState) : HashMap<string, TerminalDraft> =
        m.TerminalDrafts
        |> Map.toSeq
        |> Seq.map (fun ((terminal, author), v) -> TerminalDraftKey.make terminal author, v)
        |> HashMap.ofSeq

    let private pendingByKey (m: SyncedSessionState) : HashMap<string, PendingAct> =
        m.Pending |> Map.toSeq |> Seq.map (fun (k, v) -> QueueId.value k, v) |> HashMap.ofSeq

    /// `Create` for Ylmish's options: build the adaptive companion from a model.
    let create (m: SyncedSessionState) : AdaptiveSyncedState =
        { Drafts = cmap (draftsByKey m)
          Queue = cmap (queueByKey m)
          Title = cval m.Title
          SharedBrief = cval m.SharedBrief
          TerminalDrafts = cmap (terminalDraftsByKey m)
          Pending = cmap (pendingByKey m)
          Model = cval m.Model }

    /// `Update` for Ylmish's options: fold the next model into the companion. Setting
    /// `cmap.Value` yields keyed deltas, so only changed entries re-encode.
    let update (a: AdaptiveSyncedState) (m: SyncedSessionState) : unit =
        a.Drafts.Value <- draftsByKey m
        a.Queue.Value <- queueByKey m
        a.Title.Value <- m.Title
        a.SharedBrief.Value <- m.SharedBrief
        a.TerminalDrafts.Value <- terminalDraftsByKey m
        a.Pending.Value <- pendingByKey m
        a.Model.Value <- m.Model

    /// Per-draft encoding: the map key *is* the author (one draft per client), so `author` is
    /// re-stated only because an empty object would write no Yjs key at all (Ylmish creates a
    /// nested map lazily, only when a field writes) and the slot would never materialize. The
    /// `queueId` is the key this draft becomes when sent — it must cross the boundary, because
    /// every co-editor's send has to write the same queue key for concurrent sends to merge. The
    /// rich body is a top-level `Y.XmlFragment` root (`BodyKey.draft`), NOT nested here: a
    /// fragment in a keyed-map entry crashes Ylmish's structural decode, so it is a sibling root
    /// the app co-manages (RichText.fs).
    let private encodeDraft (d: DraftState) : Encoded =
        Encode.object
            [ "author", Encode.string (AVal.constant (PeerId.value d.Author))
              "queueId", Encode.string (AVal.constant (QueueId.value d.QueueId)) ]

    /// Per-queue-entry encoding: author (stable string) and order (an LWW float register, so
    /// reorder = one register write, never a structural move). The rich body is a top-level
    /// `Y.XmlFragment` root (`BodyKey.queued`), not nested — see `encodeDraft`.
    let private encodeQueued (q: QueuedMessage) : Encoded =
        Encode.object
            [ "author", Encode.string (AVal.constant (PeerId.value q.Author))
              "order", Encode.float (AVal.constant q.Order) ]

    let private encodeBrief (b: aval<SharedBrief>) : Encoded =
        Encode.object [ "body", Encode.string (b |> AVal.map (fun x -> x.Body)) ]

    /// A terminal composer slot. Both ids are restated even though the key carries them,
    /// for the same reason `encodeDraft` restates its author: an entry that writes no field
    /// creates no Yjs key, and a slot that materializes nothing is a slot no collaborator
    /// ever sees. The command text is a sibling `Y.Text` root (`BodyKey.terminalDraft`).
    let private encodeTerminalDraft (d: TerminalDraft) : Encoded =
        Encode.object
            [ "terminal", Encode.string (AVal.constant (TerminalId.value d.Terminal))
              "author", Encode.string (AVal.constant (PeerId.value d.Author))
              "queueId", Encode.string (AVal.constant (QueueId.value d.QueueId)) ]

    /// A queued act. `author` is an actor TOKEN, not a peer id, because the agent proposes
    /// acts too and "who asked for this" is part of the record. Its text is a sibling
    /// `Y.Text` root (`BodyKey.terminalQueued`), so nothing of the command crosses here.
    ///
    /// The `subject` key keeps the `terminal:<id>` wire form the entry has always had —
    /// the F# shape simplified to a `TerminalId` (Plan 23), the doc format did not, so a
    /// persisted doc and a pre-upgrade browser tab both keep reading.
    let private encodePendingAct (q: PendingAct) : Encoded =
        Encode.object
            [ "subject", Encode.string (AVal.constant ("terminal:" + TerminalId.value q.Terminal))
              "onBehalfOf",
              Encode.string
                  (AVal.constant
                      (Authority.onBehalfOf q.Authority |> Option.map ActorRef.token |> Option.defaultValue ""))
              "author", Encode.string (AVal.constant (ActorRef.token (Authority.author q.Authority)))
              "order", Encode.float (AVal.constant q.Order)
              // `"120x40"`, the same spelling the transcript's `r` record uses
              // (`TerminalSize.format`), so one format serves the doc and the recording and
              // neither can drift from the other. A string like `background` beside it: the
              // doc carries text, and the domain type is where it becomes a value.
              "size",
              Encode.string
                  (AVal.constant (q.Size |> Option.map TerminalSize.format |> Option.defaultValue "")) ]

    /// The session's model choice: one optional top-level REGISTER, which Ylmish lays out as
    /// a key in the argless root map rather than as a named root type (`Binding.attach`'s
    /// LAYOUT note — only structural containers get named roots). A plain string rather than
    /// a nested object for exactly that reason: the flat register is the shape whose
    /// presence and absence are both a single key, so unpicking is a delete and there is no
    /// half-written slot to read back.
    let private encodeModel (m: aval<ModelId>) : Encoded =
        Encode.string (m |> AVal.map ModelId.value)

    /// Which parts of the session sync, and how each merges. Everything else in the
    /// models — the conversation projection above all — is app-only by omission. Rich bodies
    /// are deliberately absent: they live as sibling `Y.XmlFragment` roots the app manages
    /// directly (RichText.fs), so they never enter this decoded tree.
    let encode (a: AdaptiveSyncedState) : Encoded =
        Encode.object
            [ "drafts", Encode.map encodeDraft (a.Drafts :> amap<_, _>)
              "queue", Encode.map encodeQueued (a.Queue :> amap<_, _>)
              // A top-level collaborative text: anchors to a named `title` Y.Text root, so
              // two peers naming the session offline merge rather than clobber.
              "title", Encode.text a.Title
              "sharedBrief", Encode.option encodeBrief a.SharedBrief
              "terminalDrafts", Encode.map encodeTerminalDraft (a.TerminalDrafts :> amap<_, _>)
              // Named for what they hold rather than for the one subject kind that used to
              // be the only one (Plan 15, stage 3).
              "pending", Encode.map encodePendingAct (a.Pending :> amap<_, _>)
              "model", Encode.option encodeModel a.Model ]

    /// The doc-side field shapes, before identifier validation. Bodies are omitted here: they
    /// are top-level `Y.XmlFragment` roots the app resolves via the `BodyRegistry`, never part
    /// of the decoded tree (a fragment reachable there would crash the structural reader).
    type private QueuedFields =
        { Author : string
          Order : float }

    /// The doc-side draft entry: the queue key it becomes when sent (`author` is the map key,
    /// so it is not read back).
    let private decodeDraft<'m> : Decoder<'m, string option> =
        Decode.object {
            let! queueId = Decode.object.optional "queueId" Decode.string
            return queueId
        }

    let private decodeQueued<'m> : Decoder<'m, QueuedFields> =
        Decode.object {
            let! author = Decode.object.required "author" Decode.string
            let! order = Decode.object.optional "order" Decode.float
            return
                { Author = author
                  Order = defaultArg order 0.0 }
        }

    let private decodeBrief<'m> : Decoder<'m, SharedBrief> =
        Decode.object {
            let! body = Decode.object.required "body" Decode.string
            return { SharedBrief.Body = body }
        }

    /// The doc-side pending entry, before validation.
    type private PendingFields =
        { Subject : string
          OnBehalfOf : string
          Author : string
          Order : float
          /// Whether the author is waiting on this one (Plan 20, stage 2). A string on the
          /// doc side like every other field here — the doc carries text, and the domain
          /// type is where it becomes a bool.
          Background : string
          /// The author's terminal width, `"120x40"`, or empty for an act with no viewport.
          Size : string }

    /// A terminal draft entry: the queue key it becomes when sent. Both ids come from the
    /// map key, so only this crosses.
    let private decodeTerminalDraft<'m> : Decoder<'m, string option> =
        Decode.object {
            let! queueId = Decode.object.optional "queueId" Decode.string
            return queueId
        }

    let private decodePendingAct<'m> : Decoder<'m, PendingFields> =
        Decode.object {
            let! subject = Decode.object.required "subject" Decode.string
            let! onBehalfOf = Decode.object.optional "onBehalfOf" Decode.string
            let! author = Decode.object.required "author" Decode.string
            let! order = Decode.object.optional "order" Decode.float
            let! background = Decode.object.optional "background" Decode.string
            let! size = Decode.object.optional "size" Decode.string
            return
                { Subject = subject
                  OnBehalfOf = defaultArg onBehalfOf ""
                  Author = author
                  Order = defaultArg order 0.0
                  Background = defaultArg background ""
                  Size = defaultArg size "" }
        }

    /// A model register the smart constructor refuses reads back as ABSENT — the provider's
    /// default — rather than as a model nothing can run: the fallback is always something a
    /// turn can actually do.
    let private modelToDomain (raw: string option) : ModelId option =
        raw
        |> Option.bind (fun id ->
            match ModelId.create id with
            | Ok model -> Some model
            | Error _ -> None)

    /// Entries whose identifiers fail the smart constructors are skipped rather than
    /// failing the decode: the doc is shared with peers we don't control, and a decode
    /// must stay total.
    let private draftsToDomain (h: HashMap<string, string option>) : Map<PeerId, DraftState> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, queueId) ->
            // The key is the author (one draft per client); an invalid key is skipped so
            // the decode stays total over a doc shared with peers we don't control. A slot whose
            // `queueId` is absent or invalid is skipped for the same reason: it could not be sent,
            // and a slot nobody can send is not a draft.
            match PeerId.create key, queueId |> Option.map QueueId.create with
            | Ok author, Some (Ok queueId) -> acc |> Map.add author { Author = author; QueueId = queueId }
            | _ -> acc)

    let private queueToDomain (h: HashMap<string, QueuedFields>) : Map<QueueId, QueuedMessage> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            match QueueId.create key, PeerId.create f.Author with
            | Ok id, Ok author ->
                acc |> Map.add id { QueueId = id; Author = author; Order = f.Order }
            | _ -> acc)

    let private terminalDraftsToDomain
        (h: HashMap<string, string option>)
        : Map<TerminalId * PeerId, TerminalDraft> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, queueId) ->
            // Same totality rule as the message drafts: an unparseable key or a slot with no
            // sendable queue key is skipped, never fatal.
            match TerminalDraftKey.parse key, queueId |> Option.map QueueId.create with
            | Some (terminal, author), Some (Ok queueId) ->
                acc |> Map.add (terminal, author) { Terminal = terminal; Author = author; QueueId = queueId }
            | _ -> acc)

    /// The terminal a stored subject key names. `command:*` entries — structured commands
    /// parked by a build before Plan 23 — parse to `None` and are DROPPED at decode: safer
    /// than running an act a person was still deciding on, and the one place a leftover of
    /// that shape can still arrive from.
    let private subjectTerminal (raw: string) : TerminalId option =
        if raw.StartsWith "terminal:" then
            match TerminalId.create (raw.Substring 9) with
            | Ok id -> Some id
            | Error _ -> None
        else None

    let private pendingToDomain (h: HashMap<string, PendingFields>) : Map<QueueId, PendingAct> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            match QueueId.create key, subjectTerminal f.Subject, ActorRef.ofToken f.Author with
            | Ok id, Some terminal, Some author ->
                acc
                |> Map.add
                    id
                    { QueueId = id
                      Terminal = terminal
                      Order = f.Order
                      // Recovered rather than authored: an unreadable credential owner reads
                      // as NONE, which makes the act run on nothing rather than on somebody
                      // else's — the safe direction, and the dispatch refuses it with a
                      // reason. The authoring constructors cannot express that state, which
                      // is exactly why decoding does not go through them.
                      Authority =
                        Authority.rehydrate
                            author
                            (if f.OnBehalfOf = "" then None else ActorRef.ofToken f.OnBehalfOf)
                      // Absent reads as foreground, which is what every entry a person
                      // writes is and what every entry written before Plan 20 was.
                      Background = (f.Background = "true")
                      // Unreadable or absent is NO claim rather than a guessed one: an entry
                      // written before the field existed, or by something that put nonsense
                      // there, leaves the terminal at the width it had.
                      Size = TerminalSize.parse f.Size }
            | _ -> acc)

    /// Decode the synced state out of a doc. Total, and decode-empty = init: on an empty
    /// doc every optional comes back `None` and this returns `SyncedSessionState.empty`.
    let decode<'m> : Decoder<'m, SyncedSessionState> =
        Decode.object {
            let! drafts = Decode.object.optional "drafts" (Decode.map decodeDraft)
            let! queue = Decode.object.optional "queue" (Decode.map decodeQueued)
            let! title = Decode.object.optional "title" Decode.text
            let! brief = Decode.object.optional "sharedBrief" decodeBrief
            let! terminalDrafts = Decode.object.optional "terminalDrafts" (Decode.map decodeTerminalDraft)
            let! pending = Decode.object.optional "pending" (Decode.map decodePendingAct)
            let! model = Decode.object.optional "model" Decode.string
            return
                { Drafts = drafts |> Option.map draftsToDomain |> Option.defaultValue Map.empty
                  Queue = queue |> Option.map queueToDomain |> Option.defaultValue Map.empty
                  Title = defaultArg title Text.empty
                  SharedBrief = brief
                  TerminalDrafts =
                    terminalDrafts |> Option.map terminalDraftsToDomain |> Option.defaultValue Map.empty
                  Pending = pending |> Option.map pendingToDomain |> Option.defaultValue Map.empty
                  Model = modelToDomain model }
        }

    open Fable.Core

    [<Emit("$0.share.has($1)")>]
    let private shareHas (doc: Yjs.Y.Doc) (name: string) : bool = jsNative

    [<Emit("Array.from($0.keys())")>]
    let private mapKeys (m: Yjs.Y.Map<obj>) : string[] = jsNative

    [<Emit("$0.toString()")>]
    let private textString (t: Yjs.Y.Text) : string = jsNative

    /// Yjs materializes root types created by a *remote* update as untyped placeholders
    /// until they are first `get` locally; a structural read of such a doc would miss
    /// them. Type the codec's roots (and only those that exist) before reading.
    let private materializeRoots (doc: Yjs.Y.Doc) : unit =
        if shareHas doc "drafts" then (doc.getMap "drafts" : Yjs.Y.Map<obj>) |> ignore
        if shareHas doc "queue" then (doc.getMap "queue" : Yjs.Y.Map<obj>) |> ignore
        // The title is a named `Y.Text` root, not a map — type it as text before reading.
        if shareHas doc "title" then (doc.getText "title" : Yjs.Y.Text) |> ignore
        if shareHas doc "sharedBrief" then (doc.getMap "sharedBrief" : Yjs.Y.Map<obj>) |> ignore
        if shareHas doc "terminalDrafts" then (doc.getMap "terminalDrafts" : Yjs.Y.Map<obj>) |> ignore
        if shareHas doc "pending" then (doc.getMap "pending" : Yjs.Y.Map<obj>) |> ignore

    /// Read one string field off a keyed-map entry, `""` when absent — the shape every
    /// structural read below repeats.
    let private entryString (entry: Yjs.Y.Map<obj>) (field: string) : string =
        entry.get field |> Option.map (unbox<string>) |> Option.defaultValue ""

    /// Fold every entry of a named root map through `read`. Absent root = empty.
    let private foldRoot (doc: Yjs.Y.Doc) (root: string) (read: Yjs.Y.Map<obj> -> 'a) : HashMap<string, 'a> =
        if not (shareHas doc root) then HashMap.empty
        else
            let m : Yjs.Y.Map<obj> = doc.getMap root
            (HashMap.empty, mapKeys m)
            ||> Array.fold (fun acc k ->
                match m.get k with
                | Some entryObj when not (isNull entryObj) -> HashMap.add k (read (unbox<Yjs.Y.Map<obj>> entryObj)) acc
                | _ -> acc)

    /// Read the synced state currently in a doc (the decode direction alone — used by the
    /// Session Process, which observes the doc without running its own Ylmish binding).
    ///
    /// This reads the codec's named roots (`drafts`/`queue`/`title`/`sharedBrief`) directly and
    /// structurally, rather than through a whole-doc structural decode. Rich bodies are sibling
    /// `Y.XmlFragment` roots (RichText.fs), and Ylmish's structural reader walks a fragment as a
    /// cyclic plain object — so a whole-doc read crashes the instant any body exists. Reading the
    /// known roots by hand sidesteps the body roots entirely. Total: an entry with an invalid id
    /// is skipped (`draftsToDomain`/`queueToDomain`); an absent root reads as empty.
    let ofDoc (doc: Yjs.Y.Doc) : Result<SyncedSessionState, Error list> =
        materializeRoots doc
        let draftsH =
            if shareHas doc "drafts" then
                let m : Yjs.Y.Map<obj> = doc.getMap "drafts"
                (HashMap.empty, mapKeys m)
                ||> Array.fold (fun acc k ->
                    match m.get k with
                    | Some entryObj when not (isNull entryObj) ->
                        let entry = unbox<Yjs.Y.Map<obj>> entryObj
                        HashMap.add k (entry.get "queueId" |> Option.map (unbox<string>)) acc
                    | _ -> acc)
            else HashMap.empty
        let queueH =
            if shareHas doc "queue" then
                let m : Yjs.Y.Map<obj> = doc.getMap "queue"
                (HashMap.empty, mapKeys m)
                ||> Array.fold (fun acc k ->
                    match m.get k with
                    | Some entryObj when not (isNull entryObj) ->
                        let entry = unbox<Yjs.Y.Map<obj>> entryObj
                        let author = entry.get "author" |> Option.map (unbox<string>) |> Option.defaultValue ""
                        let order = entry.get "order" |> Option.map (unbox<float>) |> Option.defaultValue 0.0
                        HashMap.add k { Author = author; Order = order } acc
                    | _ -> acc)
            else HashMap.empty
        let title =
            if shareHas doc "title" then Text.ofString (textString (doc.getText "title")) else Text.empty
        let brief =
            if shareHas doc "sharedBrief" then
                match (doc.getMap "sharedBrief" : Yjs.Y.Map<obj>).get "body" with
                | Some b when not (isNull b) -> Some { SharedBrief.Body = unbox<string> b }
                | _ -> None
            else None
        let terminalDraftsH =
            foldRoot doc "terminalDrafts" (fun entry -> entry.get "queueId" |> Option.map (unbox<string>))
        let pendingH =
            foldRoot doc "pending" (fun entry ->
                { Subject = entryString entry "subject"
                  OnBehalfOf = entryString entry "onBehalfOf"
                  Author = entryString entry "author"
                  Order = entry.get "order" |> Option.map (unbox<float>) |> Option.defaultValue 0.0
                  Background = entryString entry "background"
                  Size = entryString entry "size" })
        // Off the ARGLESS root map, not off a named root: a top-level register lives there
        // (see `encodeModel`), so `doc.getMap "model"` would silently mint an empty map and
        // read back as "nobody has chosen" for ever.
        let model =
            match (doc.getMap () : Yjs.Y.Map<obj>).get "model" with
            | Some id when not (isNull id) -> Some (unbox<string> id)
            | _ -> None
        Ok
            { Drafts = draftsToDomain draftsH
              Queue = queueToDomain queueH
              Title = title
              SharedBrief = brief
              TerminalDrafts = terminalDraftsToDomain terminalDraftsH
              Pending = pendingToDomain pendingH
              Model = modelToDomain model }

    /// The origin tag on the Session Process's own doc writes (the drain's removals),
    /// distinct from the remote-apply origin so they broadcast like any local update.
    let processOrigin : obj = box "yession-process-drain"

    /// The Session Process's one structural doc write: remove consumed queue entries, in
    /// a single transaction under the process origin (Phase 3 drain, step 2 — the doc
    /// removal after the durable append). Boundary code may touch Y types; application
    /// logic still never does. Removing an already-removed key merges as a CRDT no-op.
    let removeQueued (doc: Yjs.Y.Doc) (ids: QueueId list) : unit =
        if not (List.isEmpty ids) then
            doc.transact (
                (fun _ ->
                    let queue : Yjs.Y.Map<obj> = doc.getMap "queue"
                    ids |> List.iter (fun id -> queue.delete (QueueId.value id))),
                processOrigin)

    /// The Markdown of a queue entry's rich body, read straight from its top-level fragment
    /// root — the drain's snapshot into the durable `MessageSent` (the Session Process observes
    /// the doc without a Ylmish binding, so it reads the body fragment directly). An entry whose
    /// body was never written snapshots as the empty string.
    let queuedBodyMarkdown (doc: Yjs.Y.Doc) (id: QueueId) : string =
        Markdown.ofFragment (doc.getXmlFragment (BodyKey.queued id))

    /// The Markdown of a draft slot's rich body, read straight from its top-level fragment root
    /// (keyed by the draft's author). An empty/never-written body reads as the empty string.
    /// Used where a body must be asserted from a doc that has no client registry (e.g. a
    /// restarted Session Process).
    let draftBodyMarkdown (doc: Yjs.Y.Doc) (author: PeerId) : string =
        Markdown.ofFragment (doc.getXmlFragment (BodyKey.draft author))

    /// Whether the doc currently holds a draft slot for this author — read from the `drafts`
    /// root, not from a decoded model, so a caller reacting to a doc update reads the slot and
    /// the body at the same instant (the publication rule in `DraftSlot` needs both bits from
    /// one state, never one of them a model refresh behind).
    let hasDraft (doc: Yjs.Y.Doc) (author: PeerId) : bool =
        shareHas doc "drafts" && (doc.getMap "drafts" : Yjs.Y.Map<obj>).has (PeerId.value author)

    /// The queue key an author's draft will become when sent, read from the doc — the same value
    /// for every co-editor, which is what makes concurrent sends merge instead of duplicating.
    /// `None` when there is no slot (nothing published to send).
    let draftQueueId (doc: Yjs.Y.Doc) (author: PeerId) : QueueId option =
        if not (shareHas doc "drafts") then None
        else
            match (doc.getMap "drafts" : Yjs.Y.Map<obj>).get (PeerId.value author) with
            | Some entryObj when not (isNull entryObj) ->
                (unbox<Yjs.Y.Map<obj>> entryObj).get "queueId"
                |> Option.map (unbox<string>)
                |> Option.bind (fun value ->
                    match QueueId.create value with
                    | Ok id -> Some id
                    | Error _ -> None)
            | _ -> None

    // --- Terminals (Plan 13) -----------------------------------------------------------
    //
    // The same three moves the message queue needs, over the terminal roots: read a
    // command's text, remove consumed entries, and answer the publication rule's two
    // questions from ONE doc state. Commands are plain `Y.Text` roots, so a read is
    // `toString` rather than a Markdown serialize.

    /// The text of a queued terminal command, read straight from its root — what the drain
    /// snapshots into the durable `TerminalBlockStarted`. Never written = empty string.
    let terminalQueuedText (doc: Yjs.Y.Doc) (id: QueueId) : string =
        textString (doc.getText (BodyKey.terminalQueued id))

    /// The text of a terminal composer slot.
    let terminalDraftText (doc: Yjs.Y.Doc) (terminal: TerminalId) (author: PeerId) : string =
        textString (doc.getText (BodyKey.terminalDraft terminal author))

    /// Whether the doc announces this author's composer slot in this terminal.
    let hasTerminalDraft (doc: Yjs.Y.Doc) (terminal: TerminalId) (author: PeerId) : bool =
        shareHas doc "terminalDrafts"
        && (doc.getMap "terminalDrafts" : Yjs.Y.Map<obj>).has (TerminalDraftKey.make terminal author)

    /// The queue key an author's terminal draft becomes when sent — the same value for every
    /// co-editor, which is what makes concurrent sends merge into one entry.
    let terminalDraftQueueId (doc: Yjs.Y.Doc) (terminal: TerminalId) (author: PeerId) : QueueId option =
        if not (shareHas doc "terminalDrafts") then None
        else
            match (doc.getMap "terminalDrafts" : Yjs.Y.Map<obj>).get (TerminalDraftKey.make terminal author) with
            | Some entryObj when not (isNull entryObj) ->
                (unbox<Yjs.Y.Map<obj>> entryObj).get "queueId"
                |> Option.map (unbox<string>)
                |> Option.bind (fun value ->
                    match QueueId.create value with
                    | Ok id -> Some id
                    | Error _ -> None)
            | _ -> None

    /// Put a command in a terminal's queue, from the Session Process, in ONE transaction:
    /// the command's text root and the entry that names it (Plan 13).
    ///
    /// This is the Process's one CREATING doc write, and it earns the exception the same way
    /// the drain's removals do: the terminal queue is collaborative state, and the agent is
    /// a participant in it. Writing the command anywhere else would give the agent a private
    /// execution path — the exact thing this design removes, because a command nobody can
    /// see is a command nobody can read, edit, or withdraw.
    ///
    /// One transaction for the send's reason: the terminal drain wakes on the entry's
    /// arrival, so an entry that arrived without its text would be snapshotted as an empty
    /// command.
    let enqueueTerminalCommand
        (doc: Yjs.Y.Doc)
        (id: QueueId)
        (terminal: TerminalId)
        // Who is behind it (Plan 20). ONE value, and the only agent-shaped way to build one
        // names whose authority it runs on — so the omission this replaced, an agent command
        // enqueued with no owner at all, is no longer something a caller can forget.
        //
        // What a woken turn resolves its authority from, too: a command queued for nobody can
        // start no turn, which is the safe direction and the one an unreadable owner already
        // takes.
        (authority: Authority)
        (order: float)
        (command: string)
        // Whether the author will wait on it (Plan 20, stage 2). On the entry rather than
        // held by the caller, because the DRAIN is what mints the block that records it and
        // the drain reads the doc — a flag the caller kept would not survive the hop, nor a
        // restart between the enqueue and the run.
        (background: bool)
        : unit =
        doc.transact (
            (fun _ ->
                (doc.getText (BodyKey.terminalQueued id)).insert (0, command)
                let queue : Yjs.Y.Map<obj> = doc.getMap "pending"
                let entry : Yjs.Y.Map<obj> = Yjs.Y.Map.Create ()
                queue.set (QueueId.value id, box entry) |> ignore
                entry.set ("subject", box ("terminal:" + TerminalId.value terminal)) |> ignore
                entry.set ("author", box (ActorRef.token (Authority.author authority))) |> ignore
                entry.set ("order", box order) |> ignore
                if background then entry.set ("background", box "true") |> ignore
                Authority.onBehalfOf authority
                |> Option.iter (fun actor -> entry.set ("onBehalfOf", box (ActorRef.token actor)) |> ignore)),
            processOrigin)

    /// Remove consumed pending entries in one transaction under the process origin — the
    /// terminal drain's counterpart of `removeQueued`.
    let removePending (doc: Yjs.Y.Doc) (ids: QueueId list) : unit =
        if not (List.isEmpty ids) then
            doc.transact (
                (fun _ ->
                    let queue : Yjs.Y.Map<obj> = doc.getMap "pending"
                    ids |> List.iter (fun id -> queue.delete (QueueId.value id))),
                processOrigin)
    /// Remove every terminal composer slot whose command line is empty, returning the keys
    /// dropped. The boot-time repair `removeEmptyDrafts` performs for message drafts, for
    /// the same reason and under the same safety argument: at boot no peer is connected, so
    /// an empty command cannot be one being typed.
    let removeEmptyTerminalDrafts (doc: Yjs.Y.Doc) : (TerminalId * PeerId) list =
        materializeRoots doc
        if not (shareHas doc "terminalDrafts") then []
        else
            let drafts : Yjs.Y.Map<obj> = doc.getMap "terminalDrafts"
            let empty =
                mapKeys drafts
                |> Array.choose (fun key ->
                    match TerminalDraftKey.parse key with
                    | Some (terminal, author) when (terminalDraftText doc terminal author).Trim () = "" ->
                        Some (key, (terminal, author))
                    | _ -> None)
                |> List.ofArray
            if not (List.isEmpty empty) then
                doc.transact ((fun _ -> empty |> List.iter (fst >> drafts.delete)), processOrigin)
            empty |> List.map snd

    /// Remove every draft slot whose body has no content, returning the authors dropped.
    /// A slot is published on its author's first keystroke and retracted when their body empties
    /// (`DraftSlot`), so an empty-bodied slot is garbage: builds before that rule published a slot
    /// the moment a client mounted its composer, leaving one behind in the persisted doc for every
    /// peer that ever opened the session — each an empty draft box on everyone's composer forever.
    /// Call at boot, where no peer is connected: no keystroke can race the read, so an empty body
    /// cannot be a draft in progress. A key that is not a valid `PeerId` is left alone — the decode
    /// already skips it, and it is not ours to interpret.
    let removeEmptyDrafts (doc: Yjs.Y.Doc) : PeerId list =
        materializeRoots doc
        if not (shareHas doc "drafts") then []
        else
            let drafts : Yjs.Y.Map<obj> = doc.getMap "drafts"
            let empty =
                mapKeys drafts
                |> Array.choose (fun key ->
                    match PeerId.create key with
                    | Ok author when (draftBodyMarkdown doc author).Trim () = "" -> Some author
                    | _ -> None)
                |> List.ofArray
            if not (List.isEmpty empty) then
                doc.transact (
                    (fun _ -> empty |> List.iter (fun author -> drafts.delete (PeerId.value author))),
                    processOrigin)
            empty

/// Moves Yjs updates over the transport's opaque `State` frame. The wire payload is a
/// base64-encoded Yjs update; lib0 (a yjs dependency) provides base64 in both Node and
/// browsers. Runs only under Fable — .NET builds only type-check it.
module DocSync =

    open Fable.Core
    open Yjs

    [<Import("toBase64", "lib0/buffer")>]
    let private toBase64 (bytes: JS.Uint8Array) : string = jsNative

    [<Import("fromBase64", "lib0/buffer")>]
    let private fromBase64 (s: string) : JS.Uint8Array = jsNative

    [<Emit("$0 === $1")>]
    let private refEq (a: obj) (b: obj) : bool = jsNative

    [<Emit("$0.on('update', $1)")>]
    let private addUpdateListener (doc: Y.Doc) (handler: JS.Uint8Array -> obj -> unit) : unit = jsNative

    [<Emit("$0.off('update', $1)")>]
    let private removeUpdateListener (doc: Y.Doc) (handler: JS.Uint8Array -> obj -> unit) : unit = jsNative

    /// Register a doc listener and get back the way to stop it. ONE verb, because `off`
    /// only removes a listener when handed the very function reference `on` was given —
    /// a caller who kept the handler and remembered to pass it again is a caller who can
    /// forget. The disposer closes over it, so there is nothing left to get wrong.
    let private onUpdate (doc: Y.Doc) (handler: JS.Uint8Array -> obj -> unit) : unit -> unit =
        addUpdateListener doc handler
        fun () -> removeUpdateListener doc handler

    /// The origin tag under which remote payloads are applied, letting the local-update
    /// broadcast tell relayed changes from locally-originated ones.
    let private remoteOrigin : obj = box "yession-remote-state"

    /// The full current doc state as one wire payload. Full-state updates are idempotent
    /// and order-independent, so this is the safe initial exchange.
    let fullState (doc: Y.Doc) : string = toBase64 (Y.encodeStateAsUpdate doc)

    /// Apply a remote peer's payload to the local doc.
    let applyRemote (doc: Y.Doc) (payload: string) : unit =
        Y.applyUpdate (doc, fromBase64 payload, remoteOrigin)

    /// Invoke `send` with every locally-originated update (anything not applied via
    /// `applyRemote`, including the Ylmish binding's writes). Remote payloads are excluded
    /// so a peer never echoes back what it was just sent; relaying to *other* peers is the
    /// hub's explicit job.
    ///
    /// Returns the way to stop sending. A registration that outlives what it sends over is
    /// a leak with a symptom nobody attributes: the doc keeps calling a listener whose
    /// channel is shut, and every local update walks one more of them per reconnect.
    let onLocalUpdate (doc: Y.Doc) (send: string -> unit) : unit -> unit =
        onUpdate doc (fun update origin ->
            if not (refEq origin remoteOrigin) then send (toBase64 update))

    /// Invoke `handle` after every doc update, however it originated.
    let onAnyUpdate (doc: Y.Doc) (handle: unit -> unit) : unit =
        onUpdate doc (fun _ _ -> handle ()) |> ignore

    /// Invoke `persist` with every update applied to the doc, however it originated —
    /// the doc-persistence tap (Step 19). Register it before the observers that act on
    /// updates, so durability precedes visibility.
    let onAnyUpdatePayload (doc: Y.Doc) (persist: string -> unit) : unit =
        onUpdate doc (fun update _ -> persist (toBase64 update)) |> ignore
