namespace Yession.Domain

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
type AdaptiveSyncedState =
    { Drafts : cmap<string, DraftState>
      Queue : cmap<string, QueuedMessage>
      Title : cval<Text>
      SharedBrief : cval<SharedBrief option>
      TerminalDrafts : cmap<string, TerminalDraft>
      Pending : cmap<string, PendingAct>
      Model : cval<ModelId option>
      Gates : cmap<string, ApprovalMode>
      TerminalSizes : cmap<string, TerminalSize> }

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

    let private gatesByKey (m: SyncedSessionState) : HashMap<string, ApprovalMode> =
        m.Gates |> Map.toSeq |> Seq.map (fun (k, v) -> GateSubject.describe k, v) |> HashMap.ofSeq

    let private terminalSizesByKey (m: SyncedSessionState) : HashMap<string, TerminalSize> =
        m.TerminalSizes |> Map.toSeq |> Seq.map (fun (k, v) -> TerminalId.value k, v) |> HashMap.ofSeq

    /// `Create` for Ylmish's options: build the adaptive companion from a model.
    let create (m: SyncedSessionState) : AdaptiveSyncedState =
        { Drafts = cmap (draftsByKey m)
          Queue = cmap (queueByKey m)
          Title = cval m.Title
          SharedBrief = cval m.SharedBrief
          TerminalDrafts = cmap (terminalDraftsByKey m)
          Pending = cmap (pendingByKey m)
          Model = cval m.Model
          Gates = cmap (gatesByKey m)
          TerminalSizes = cmap (terminalSizesByKey m) }

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
        a.Gates.Value <- gatesByKey m
        a.TerminalSizes.Value <- terminalSizesByKey m

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

    /// An act waiting on a verdict. `author` is an actor TOKEN, not a peer id, because the
    /// agent proposes acts too and "who asked for this" is what the approval policy reads.
    /// `approvedBy` is the approval itself — an LWW register, so two peers approving at
    /// once settle on one of them rather than on a conflict.
    ///
    /// A `CommandLine`'s text is a sibling `Y.Text` root (`BodyKey.terminalQueued`), so the
    /// payload here carries only which KIND it is; a `CommandCall` has no editable text and
    /// carries its rendered arguments inline, because nobody is going to merge them.
    let private encodePendingAct (q: PendingAct) : Encoded =
        let tool, args, summary =
            match q.Payload with
            | CommandLine -> "", "", ""
            | CommandCall (tool, args, summary) -> tool, args, summary
        Encode.object
            [ "subject", Encode.string (AVal.constant (GateSubject.describe q.Subject))
              "payload",
              Encode.string (AVal.constant (match q.Payload with CommandLine -> "line" | CommandCall _ -> "call"))
              "tool", Encode.string (AVal.constant tool)
              // The arguments the call was made with, so a process that did not propose the
              // act can still carry it out. Never a credential: `onBehalfOf` names WHOSE,
              // and what it IS is resolved at execution, never replicated.
              "args", Encode.string (AVal.constant args)
              "summary", Encode.string (AVal.constant summary)
              "onBehalfOf",
              Encode.string
                  (AVal.constant
                      (Authority.onBehalfOf q.Authority |> Option.map ActorRef.token |> Option.defaultValue ""))
              "author", Encode.string (AVal.constant (ActorRef.token (Authority.author q.Authority)))
              "order", Encode.float (AVal.constant q.Order)
              "approvedBy",
              Encode.string (AVal.constant (q.ApprovedBy |> Option.map PeerId.value |> Option.defaultValue ""))
              "rejectedBy",
              Encode.string (AVal.constant (q.RejectedBy |> Option.map PeerId.value |> Option.defaultValue ""))
              "rejectedReason",
              Encode.string (AVal.constant (q.RejectedReason |> Option.defaultValue "")) ]

    let private encodeTerminalSize (s: TerminalSize) : Encoded =
        Encode.object
            [ "cols", Encode.int (AVal.constant s.Cols)
              "rows", Encode.int (AVal.constant s.Rows) ]

    /// The session's model choice: one optional top-level REGISTER, which Ylmish lays out as
    /// a key in the argless root map rather than as a named root type (`Binding.attach`'s
    /// LAYOUT note — only structural containers get named roots). A plain string rather than
    /// a nested object for exactly that reason: the flat register is the shape whose
    /// presence and absence are both a single key, so unpicking is a delete and there is no
    /// half-written slot to read back.
    let private encodeModel (m: aval<ModelId>) : Encoded =
        Encode.string (m |> AVal.map ModelId.value)

    let private encodeGate (m: ApprovalMode) : Encoded =
        Encode.object [ "mode", Encode.string (AVal.constant (ApprovalMode.describe m)) ]

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
              // be the only one (Plan 15, stage 3). A doc written before that carries the
              // old `terminalQueue`/`terminalModes` roots; `migrateGateRoots` moves them at
              // boot, so there is exactly one live location, never two.
              "pending", Encode.map encodePendingAct (a.Pending :> amap<_, _>)
              "model", Encode.option encodeModel a.Model
              "gates", Encode.map encodeGate (a.Gates :> amap<_, _>)
              "terminalSizes", Encode.map encodeTerminalSize (a.TerminalSizes :> amap<_, _>) ]

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
          Payload : string
          Tool : string
          Args : string
          Summary : string
          OnBehalfOf : string
          Author : string
          Order : float
          ApprovedBy : string
          RejectedBy : string
          RejectedReason : string
          /// Whether the author is waiting on this one (Plan 20, stage 2). A string on the
          /// doc side like every other field here — the doc carries text, and the domain
          /// type is where it becomes a bool.
          Background : string }

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
            let! payload = Decode.object.optional "payload" Decode.string
            let! tool = Decode.object.optional "tool" Decode.string
            let! args = Decode.object.optional "args" Decode.string
            let! summary = Decode.object.optional "summary" Decode.string
            let! onBehalfOf = Decode.object.optional "onBehalfOf" Decode.string
            let! author = Decode.object.required "author" Decode.string
            let! order = Decode.object.optional "order" Decode.float
            let! approvedBy = Decode.object.optional "approvedBy" Decode.string
            let! rejectedBy = Decode.object.optional "rejectedBy" Decode.string
            let! rejectedReason = Decode.object.optional "rejectedReason" Decode.string
            let! background = Decode.object.optional "background" Decode.string
            return
                { Subject = subject
                  Payload = defaultArg payload ""
                  Tool = defaultArg tool ""
                  Args = defaultArg args ""
                  Summary = defaultArg summary ""
                  OnBehalfOf = defaultArg onBehalfOf ""
                  Author = author
                  Order = defaultArg order 0.0
                  ApprovedBy = defaultArg approvedBy ""
                  RejectedBy = defaultArg rejectedBy ""
                  RejectedReason = defaultArg rejectedReason ""
                  Background = defaultArg background "" }
        }

    let private decodeTerminalSize<'m> : Decoder<'m, TerminalSize> =
        Decode.object {
            let! cols = Decode.object.optional "cols" Decode.int
            let! rows = Decode.object.optional "rows" Decode.int
            return { Cols = defaultArg cols 0; Rows = defaultArg rows 0 }
        }

    /// A model register the smart constructor refuses reads back as ABSENT — the provider's
    /// default — rather than as a model nothing can run. Same direction as an unreadable
    /// gate mode: the fallback is always something a turn can actually do.
    let private modelToDomain (raw: string option) : ModelId option =
        raw
        |> Option.bind (fun id ->
            match ModelId.create id with
            | Ok model -> Some model
            | Error _ -> None)

    let private decodeGate<'m> : Decoder<'m, string> =
        Decode.object {
            let! mode = Decode.object.required "mode" Decode.string
            return mode
        }

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

    let private pendingToDomain (h: HashMap<string, PendingFields>) : Map<QueueId, PendingAct> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            match QueueId.create key, GateSubject.parse f.Subject, ActorRef.ofToken f.Author with
            | Ok id, Some subject, Some author ->
                // An empty/invalid approver reads as "not approved". Never as an approval:
                // failing OPEN on a value we could not read would run an agent's command
                // that nobody signed off, which is the one direction this must not fail in.
                let approvedBy =
                    if f.ApprovedBy = "" then None
                    else match PeerId.create f.ApprovedBy with Ok p -> Some p | Error _ -> None
                // A refusal reads the same way, and the direction is worth stating because
                // it is NOT the same safety argument. An unreadable approver fails safe by
                // construction — no approval, nothing runs. An unreadable refuser falls
                // back to the approval gate instead, which under `AutoRun` means the
                // command runs. That is acceptable only because the value round-trips
                // through our own encoder (`PeerId.value`), so a non-empty one that will
                // not parse means a doc somebody corrupted by hand rather than anything a
                // replica can produce — and in that case the entry has exactly the
                // protection it had before the refusal was written.
                let rejectedBy =
                    if f.RejectedBy = "" then None
                    else match PeerId.create f.RejectedBy with Ok p -> Some p | Error _ -> None
                // An unreadable payload kind reads as a command LINE, which is what a
                // pending act has been since Plan 13 and the only kind with an editable
                // text root. A call whose kind failed to read would otherwise render as a
                // card with no arguments on it — approving something you cannot see.
                let payload =
                    match f.Payload with
                    | "call" when f.Tool <> "" -> CommandCall (f.Tool, f.Args, f.Summary)
                    | _ -> CommandLine
                acc
                |> Map.add
                    id
                    { QueueId = id
                      Subject = subject
                      Order = f.Order
                      Payload = payload
                      // Recovered rather than authored: an unreadable credential owner reads
                      // as NONE, which makes the act run on nothing rather than on somebody
                      // else's — the safe direction, and the dispatch refuses it with a
                      // reason. The authoring constructors cannot express that state, which
                      // is exactly why decoding does not go through them.
                      Authority =
                        Authority.rehydrate
                            author
                            (if f.OnBehalfOf = "" then None else ActorRef.ofToken f.OnBehalfOf)
                            // The approval is a verdict register on the entry, below — it
                            // joins the authority when the drain acts on it, not before.
                            None
                      ApprovedBy = approvedBy
                      RejectedBy = rejectedBy
                      RejectedReason = (if f.RejectedReason = "" then None else Some f.RejectedReason)
                      // Absent reads as foreground, which is what every entry a person
                      // writes is and what every entry written before Plan 20 was.
                      Background = (f.Background = "true") }
            | _ -> acc)

    let private terminalSizesToDomain (h: HashMap<string, TerminalSize>) : Map<TerminalId, TerminalSize> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, size) ->
            match TerminalId.create key with
            // An unusable size is dropped, which reads back as the DEFAULT rather than as a
            // terminal zero columns wide. Same direction as an unreadable mode: absent means
            // the default, and the default is always something a terminal can be.
            | Ok terminal when TerminalSize.isValid size -> acc |> Map.add terminal size
            | _ -> acc)

    let private gatesToDomain (h: HashMap<string, string>) : Map<GateSubject, ApprovalMode> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, raw) ->
            match GateSubject.parse key, ApprovalMode.parse raw with
            | Some subject, Some mode -> acc |> Map.add subject mode
            // An unreadable mode is dropped, which reads back as the subject's DEFAULT
            // rather than as no gate — the safe direction again, and for a terminal the
            // default is `ApproveAgent`.
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
            let! gates = Decode.object.optional "gates" (Decode.map decodeGate)
            let! terminalSizes = Decode.object.optional "terminalSizes" (Decode.map decodeTerminalSize)
            return
                { Drafts = drafts |> Option.map draftsToDomain |> Option.defaultValue Map.empty
                  Queue = queue |> Option.map queueToDomain |> Option.defaultValue Map.empty
                  Title = defaultArg title Text.empty
                  SharedBrief = brief
                  TerminalDrafts =
                    terminalDrafts |> Option.map terminalDraftsToDomain |> Option.defaultValue Map.empty
                  Pending = pending |> Option.map pendingToDomain |> Option.defaultValue Map.empty
                  Model = modelToDomain model
                  Gates = gates |> Option.map gatesToDomain |> Option.defaultValue Map.empty
                  TerminalSizes =
                    terminalSizes |> Option.map terminalSizesToDomain |> Option.defaultValue Map.empty }
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
        if shareHas doc "gates" then (doc.getMap "gates" : Yjs.Y.Map<obj>) |> ignore

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
                  Payload = entryString entry "payload"
                  Tool = entryString entry "tool"
                  Args = entryString entry "args"
                  Summary = entryString entry "summary"
                  OnBehalfOf = entryString entry "onBehalfOf"
                  Author = entryString entry "author"
                  Order = entry.get "order" |> Option.map (unbox<float>) |> Option.defaultValue 0.0
                  ApprovedBy = entryString entry "approvedBy"
                  RejectedBy = entryString entry "rejectedBy"
                  RejectedReason = entryString entry "rejectedReason"
                  Background = entryString entry "background" })
        // Off the ARGLESS root map, not off a named root: a top-level register lives there
        // (see `encodeModel`), so `doc.getMap "model"` would silently mint an empty map and
        // read back as "nobody has chosen" for ever.
        let model =
            match (doc.getMap () : Yjs.Y.Map<obj>).get "model" with
            | Some id when not (isNull id) -> Some (unbox<string> id)
            | _ -> None
        let gatesH = foldRoot doc "gates" (fun entry -> entryString entry "mode")
        let terminalSizesH =
            foldRoot doc "terminalSizes" (fun entry ->
                { Cols = entry.get "cols" |> Option.map (unbox<int>) |> Option.defaultValue 0
                  Rows = entry.get "rows" |> Option.map (unbox<int>) |> Option.defaultValue 0 })
        Ok
            { Drafts = draftsToDomain draftsH
              Queue = queueToDomain queueH
              Title = title
              SharedBrief = brief
              TerminalDrafts = terminalDraftsToDomain terminalDraftsH
              Pending = pendingToDomain pendingH
              Model = modelToDomain model
              Gates = gatesToDomain gatesH
              TerminalSizes = terminalSizesToDomain terminalSizesH }

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
    /// see is a command nobody can approve.
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
                entry.set ("subject", box (GateSubject.describe (ForTerminal terminal))) |> ignore
                entry.set ("payload", box "line") |> ignore
                entry.set ("author", box (ActorRef.token (Authority.author authority))) |> ignore
                entry.set ("order", box order) |> ignore
                if background then entry.set ("background", box "true") |> ignore
                Authority.onBehalfOf authority
                |> Option.iter (fun actor -> entry.set ("onBehalfOf", box (ActorRef.token actor)) |> ignore)
                // Never approved on arrival: whether an approval is REQUIRED is the mode's
                // question, and pre-answering it here would let the agent approve itself.
                entry.set ("approvedBy", box "") |> ignore),
            processOrigin)

    /// Park a structured command where everyone can see it (Plan 15, stage 3b). The command
    /// gate's counterpart of `enqueueTerminalCommand`, and it earns the same exception for
    /// the same reason: the pending list is collaborative state, the agent is a participant
    /// in it, and an act nobody can see is an act nobody can refuse.
    ///
    /// No text root, and that is the payload difference rather than an omission: a call's
    /// arguments are typed, so what crosses is what a human should READ, and there is
    /// nothing here for two peers to merge.
    let enqueueCommandCall
        (doc: Yjs.Y.Doc)
        (id: QueueId)
        (tool: string)
        (args: string)
        (summary: string)
        (authority: Authority)
        (order: float)
        : unit =
        doc.transact (
            (fun _ ->
                let pending : Yjs.Y.Map<obj> = doc.getMap "pending"
                let entry : Yjs.Y.Map<obj> = Yjs.Y.Map.Create ()
                pending.set (QueueId.value id, box entry) |> ignore
                entry.set ("subject", box (GateSubject.describe (ForCommand tool))) |> ignore
                entry.set ("payload", box "call") |> ignore
                entry.set ("tool", box tool) |> ignore
                entry.set ("args", box args) |> ignore
                entry.set ("summary", box summary) |> ignore
                entry.set
                    ("onBehalfOf",
                     box (Authority.onBehalfOf authority |> Option.map ActorRef.token |> Option.defaultValue ""))
                |> ignore
                entry.set ("author", box (ActorRef.token (Authority.author authority))) |> ignore
                entry.set ("order", box order) |> ignore
                // Never approved on arrival, for `enqueueTerminalCommand`'s reason: whether
                // an approval is REQUIRED is the mode's question, and pre-answering it here
                // would let the agent approve itself.
                entry.set ("approvedBy", box "") |> ignore),
            processOrigin)

    /// Set a subject's approval mode from the Process (Plan 13, stage 3b; Plan 15, stage 3).
    ///
    /// Peers set the mode through the Ylmish binding, from the model; the Process has no
    /// model, and it needs to write modes it alone knows — the agent terminal's `AutoRun` at
    /// the moment it opens one, and whatever the operator's boot configuration named. Under
    /// the process origin, like its other structural writes, so the Process's own change is
    /// not mistaken for a peer's.
    let setGate (doc: Yjs.Y.Doc) (subject: GateSubject) (mode: ApprovalMode) : unit =
        doc.transact (
            (fun _ ->
                let modes : Yjs.Y.Map<obj> = doc.getMap "gates"
                let entry : Yjs.Y.Map<obj> = Yjs.Y.Map.Create ()
                modes.set (GateSubject.describe subject, box entry) |> ignore
                entry.set ("mode", box (ApprovalMode.describe mode)) |> ignore),
            processOrigin)

    /// Remove consumed pending entries in one transaction under the process origin — the
    /// terminal drain's counterpart of `removeQueued`, and now also the command gate's.
    let removePending (doc: Yjs.Y.Doc) (ids: QueueId list) : unit =
        if not (List.isEmpty ids) then
            doc.transact (
                (fun _ ->
                    let queue : Yjs.Y.Map<obj> = doc.getMap "pending"
                    ids |> List.iter (fun id -> queue.delete (QueueId.value id))),
                processOrigin)

    /// Move a doc written before Plan 15 stage 3 onto the widened roots, returning how many
    /// entries each carried. A boot repair, run where `removeEmptyDrafts` runs and for the
    /// same reason: no peer is connected yet, so nothing is mid-edit.
    ///
    /// It exists to avoid the one thing a bare rename would do quietly — drop a terminal
    /// whose mode somebody set to `ApproveAll` back to the default, which is LESS gated than
    /// what they asked for. The old roots are deleted as they are copied, so a doc has the
    /// entries in exactly one place afterwards rather than in two that can disagree.
    let migrateGateRoots (doc: Yjs.Y.Doc) : int * int =
        materializeRoots doc
        if shareHas doc "terminalQueue" then (doc.getMap "terminalQueue" : Yjs.Y.Map<obj>) |> ignore
        if shareHas doc "terminalModes" then (doc.getMap "terminalModes" : Yjs.Y.Map<obj>) |> ignore

        /// Every entry of a legacy root, read before the transaction so the copy and the
        /// delete below iterate a fixed list rather than a map they are mutating.
        let entriesOf (root: string) (read: Yjs.Y.Map<obj> -> 'a) : (string * 'a) list =
            if not (shareHas doc root) then []
            else
                let m : Yjs.Y.Map<obj> = doc.getMap root
                mapKeys m
                |> Array.choose (fun key ->
                    match m.get key with
                    | Some entryObj when not (isNull entryObj) -> Some (key, read (unbox<Yjs.Y.Map<obj>> entryObj))
                    | _ -> None)
                |> List.ofArray

        // Every legacy entry is a terminal command line — that is the only kind that
        // existed — so the subject comes from its `terminal` field and the payload is known
        // without reading anything. Snapshotted as plain values here, so the write below
        // touches no Y type it did not create.
        let legacyActs =
            entriesOf "terminalQueue" (fun entry ->
                {| Terminal = entryString entry "terminal"
                   Author = entryString entry "author"
                   Order = entry.get "order" |> Option.map (unbox<float>) |> Option.defaultValue 0.0
                   ApprovedBy = entryString entry "approvedBy"
                   RejectedBy = entryString entry "rejectedBy"
                   RejectedReason = entryString entry "rejectedReason" |})
        let legacyGates = entriesOf "terminalModes" (fun entry -> entryString entry "mode")

        if List.isEmpty legacyActs && List.isEmpty legacyGates then (0, 0)
        else
            doc.transact (
                (fun _ ->
                    if not (List.isEmpty legacyActs) then
                        let pending : Yjs.Y.Map<obj> = doc.getMap "pending"
                        let old : Yjs.Y.Map<obj> = doc.getMap "terminalQueue"
                        for key, act in legacyActs do
                            if act.Terminal <> "" then
                                let moved : Yjs.Y.Map<obj> = Yjs.Y.Map.Create ()
                                pending.set (key, box moved) |> ignore
                                moved.set ("subject", box ("terminal:" + act.Terminal)) |> ignore
                                moved.set ("payload", box "line") |> ignore
                                moved.set ("author", box act.Author) |> ignore
                                moved.set ("order", box act.Order) |> ignore
                                moved.set ("approvedBy", box act.ApprovedBy) |> ignore
                                moved.set ("rejectedBy", box act.RejectedBy) |> ignore
                                moved.set ("rejectedReason", box act.RejectedReason) |> ignore
                            old.delete key
                    if not (List.isEmpty legacyGates) then
                        let gates : Yjs.Y.Map<obj> = doc.getMap "gates"
                        let old : Yjs.Y.Map<obj> = doc.getMap "terminalModes"
                        for key, mode in legacyGates do
                            if mode <> "" then
                                let moved : Yjs.Y.Map<obj> = Yjs.Y.Map.Create ()
                                gates.set ("terminal:" + key, box moved) |> ignore
                                moved.set ("mode", box mode) |> ignore
                            old.delete key),
                processOrigin)
            (List.length legacyActs, List.length legacyGates)

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
    let private onUpdate (doc: Y.Doc) (handler: JS.Uint8Array -> obj -> unit) : unit = jsNative

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
    let onLocalUpdate (doc: Y.Doc) (send: string -> unit) : unit =
        onUpdate doc (fun update origin ->
            if not (refEq origin remoteOrigin) then send (toBase64 update))

    /// Invoke `handle` after every doc update, however it originated.
    let onAnyUpdate (doc: Y.Doc) (handle: unit -> unit) : unit =
        onUpdate doc (fun _ _ -> handle ())

    /// Invoke `persist` with every update applied to the doc, however it originated —
    /// the doc-persistence tap (Step 19). Register it before the observers that act on
    /// updates, so durability precedes visibility.
    let onAnyUpdatePayload (doc: Y.Doc) (persist: string -> unit) : unit =
        onUpdate doc (fun update _ -> persist (toBase64 update))
