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
      SharedBrief : cval<SharedBrief option> }

module SyncedStateSync =

    let private draftsByKey (m: SyncedSessionState) : HashMap<string, DraftState> =
        m.Drafts |> Map.toSeq |> Seq.map (fun (k, v) -> PeerId.value k, v) |> HashMap.ofSeq

    let private queueByKey (m: SyncedSessionState) : HashMap<string, QueuedMessage> =
        m.Queue |> Map.toSeq |> Seq.map (fun (k, v) -> QueueId.value k, v) |> HashMap.ofSeq

    /// `Create` for Ylmish's options: build the adaptive companion from a model.
    let create (m: SyncedSessionState) : AdaptiveSyncedState =
        { Drafts = cmap (draftsByKey m)
          Queue = cmap (queueByKey m)
          Title = cval m.Title
          SharedBrief = cval m.SharedBrief }

    /// `Update` for Ylmish's options: fold the next model into the companion. Setting
    /// `cmap.Value` yields keyed deltas, so only changed entries re-encode.
    let update (a: AdaptiveSyncedState) (m: SyncedSessionState) : unit =
        a.Drafts.Value <- draftsByKey m
        a.Queue.Value <- queueByKey m
        a.Title.Value <- m.Title
        a.SharedBrief.Value <- m.SharedBrief

    /// Per-draft encoding: the map key *is* the author (one draft per client). The body is a
    /// rich-text `Y.XmlFragment` anchored via `Encode.custom` over a `RichBody` (keyed stably
    /// by `BodyKey.draft`, so every replica binds the same fragment). The body value never
    /// decodes — the app resolves the live fragment from the registry — so nothing else crosses.
    let private encodeDraft (registry: BodyRegistry) (d: DraftState) : Encoded =
        Encode.object [ "body", Encode.custom (registry.GetOrCreate (BodyKey.draft d.Author) :> CustomElement) ]

    /// Per-queue-entry encoding: rich body via `Encode.custom`; order is an LWW float register,
    /// so reorder = one register write (never a structural move). Author is a stable string.
    let private encodeQueued (registry: BodyRegistry) (q: QueuedMessage) : Encoded =
        Encode.object
            [ "author", Encode.string (AVal.constant (PeerId.value q.Author))
              "order", Encode.float (AVal.constant q.Order)
              "body", Encode.custom (registry.GetOrCreate (BodyKey.queued q.QueueId) :> CustomElement) ]

    let private encodeBrief (b: aval<SharedBrief>) : Encoded =
        Encode.object [ "body", Encode.string (b |> AVal.map (fun x -> x.Body)) ]

    /// Which parts of the session sync, and how each merges. Everything else in the
    /// models — the conversation projection above all — is app-only by omission. `registry`
    /// supplies each body's `RichBody` (the client's; the Session Process reads the doc
    /// directly and never encodes).
    let encode (registry: BodyRegistry) (a: AdaptiveSyncedState) : Encoded =
        Encode.object
            [ "drafts", Encode.map (encodeDraft registry) (a.Drafts :> amap<_, _>)
              "queue", Encode.map (encodeQueued registry) (a.Queue :> amap<_, _>)
              // A top-level collaborative text: anchors to a named `title` Y.Text root, so
              // two peers naming the session offline merge rather than clobber.
              "title", Encode.text a.Title
              "sharedBrief", Encode.option encodeBrief a.SharedBrief ]

    /// The doc-side field shapes, before identifier validation. Bodies are omitted: a custom
    /// nested in a keyed map does not round-trip Ylmish's structural decode, so the body's
    /// live fragment is resolved from the `BodyRegistry` by the app, never decoded here.
    type private QueuedFields =
        { Author : string
          Order : float }

    let private decodeDraft<'m> : Decoder<'m, unit> =
        Decode.object { return () }

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

    /// Entries whose identifiers fail the smart constructors are skipped rather than
    /// failing the decode: the doc is shared with peers we don't control, and a decode
    /// must stay total.
    let private draftsToDomain (h: HashMap<string, unit>) : Map<PeerId, DraftState> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, _) ->
            // The key is the author (one draft per client); an invalid key is skipped so
            // the decode stays total over a doc shared with peers we don't control.
            match PeerId.create key with
            | Ok author -> acc |> Map.add author { Author = author }
            | Error _ -> acc)

    let private queueToDomain (h: HashMap<string, QueuedFields>) : Map<QueueId, QueuedMessage> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            match QueueId.create key, PeerId.create f.Author with
            | Ok id, Ok author ->
                acc |> Map.add id { QueueId = id; Author = author; Order = f.Order }
            | _ -> acc)

    /// Decode the synced state out of a doc. Total, and decode-empty = init: on an empty
    /// doc every optional comes back `None` and this returns `SyncedSessionState.empty`.
    let decode<'m> : Decoder<'m, SyncedSessionState> =
        Decode.object {
            let! drafts = Decode.object.optional "drafts" (Decode.map decodeDraft)
            let! queue = Decode.object.optional "queue" (Decode.map decodeQueued)
            let! title = Decode.object.optional "title" Decode.text
            let! brief = Decode.object.optional "sharedBrief" decodeBrief
            return
                { Drafts = drafts |> Option.map draftsToDomain |> Option.defaultValue Map.empty
                  Queue = queue |> Option.map queueToDomain |> Option.defaultValue Map.empty
                  Title = defaultArg title Text.empty
                  SharedBrief = brief }
        }

    open Fable.Core

    [<Emit("$0.share.has($1)")>]
    let private shareHas (doc: Yjs.Y.Doc) (name: string) : bool = jsNative

    /// Yjs materializes root types created by a *remote* update as untyped placeholders
    /// until they are first `get` locally; a structural read of such a doc would miss
    /// them. Type the codec's roots (and only those that exist) before reading.
    let private materializeRoots (doc: Yjs.Y.Doc) : unit =
        if shareHas doc "drafts" then (doc.getMap "drafts" : Yjs.Y.Map<obj>) |> ignore
        if shareHas doc "queue" then (doc.getMap "queue" : Yjs.Y.Map<obj>) |> ignore
        // The title is a named `Y.Text` root, not a map — type it as text before reading.
        if shareHas doc "title" then (doc.getText "title" : Yjs.Y.Text) |> ignore
        if shareHas doc "sharedBrief" then (doc.getMap "sharedBrief" : Yjs.Y.Map<obj>) |> ignore

    /// Read the synced state currently in a doc (the decode direction alone — used by the
    /// Session Process, which observes the doc without running its own Ylmish binding).
    let ofDoc (doc: Yjs.Y.Doc) : Result<SyncedSessionState, Error list> =
        materializeRoots doc
        Decode.run SyncedSessionState.empty decode doc

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

    /// The Markdown of a queue entry's rich body, read straight from the doc — the drain's
    /// snapshot into the durable `MessageSent` (the Session Process observes the doc without a
    /// Ylmish binding, so it reads the nested `Y.XmlFragment` directly). An entry whose body
    /// was never materialized (empty) snapshots as the empty string.
    let queuedBodyMarkdown (doc: Yjs.Y.Doc) (id: QueueId) : string =
        materializeRoots doc
        let queue : Yjs.Y.Map<obj> = doc.getMap "queue"
        match queue.get (QueueId.value id) with
        | Some entryObj ->
            match (unbox<Yjs.Y.Map<obj>> entryObj).get "body" with
            | Some frag when not (isNull frag) -> Markdown.ofFragment (unbox<Yjs.Y.XmlFragment> frag)
            | _ -> ""
        | None -> ""

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
