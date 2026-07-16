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
          SharedBrief = cval m.SharedBrief }

    /// `Update` for Ylmish's options: fold the next model into the companion. Setting
    /// `cmap.Value` yields keyed deltas, so only changed entries re-encode.
    let update (a: AdaptiveSyncedState) (m: SyncedSessionState) : unit =
        a.Drafts.Value <- draftsByKey m
        a.Queue.Value <- queueByKey m
        a.SharedBrief.Value <- m.SharedBrief

    /// Per-draft encoding: the map key *is* the author (one draft per client), so only the
    /// body crosses — collaborative text, concurrent edits to the same slot interleave.
    let private encodeDraft (d: DraftState) : Encoded =
        Encode.object [ "body", Encode.text (AVal.constant d.Body) ]

    /// Per-queue-entry encoding: body is collaborative text; order is an LWW float
    /// register, so reorder = one register write (never a structural move).
    let private encodeQueued (q: QueuedMessage) : Encoded =
        Encode.object
            [ "author", Encode.string (AVal.constant (PeerId.value q.Author))
              "body", Encode.text (AVal.constant q.Body)
              "order", Encode.float (AVal.constant q.Order) ]

    let private encodeBrief (b: aval<SharedBrief>) : Encoded =
        Encode.object [ "body", Encode.string (b |> AVal.map (fun x -> x.Body)) ]

    /// Which parts of the session sync, and how each merges. Everything else in the
    /// models — the conversation projection above all — is app-only by omission.
    let encode (a: AdaptiveSyncedState) : Encoded =
        Encode.object
            [ "drafts", Encode.map encodeDraft (a.Drafts :> amap<_, _>)
              "queue", Encode.map encodeQueued (a.Queue :> amap<_, _>)
              "sharedBrief", Encode.option encodeBrief a.SharedBrief ]

    /// The doc-side field shapes, before identifier validation.
    type private DraftFields =
        { Body : Text }

    type private QueuedFields =
        { Author : string
          Body : Text
          Order : float }

    let private decodeDraft<'m> : Decoder<'m, DraftFields> =
        Decode.object {
            let! body = Decode.object.optional "body" Decode.text
            return { Body = defaultArg body Text.empty }
        }

    let private decodeQueued<'m> : Decoder<'m, QueuedFields> =
        Decode.object {
            let! author = Decode.object.required "author" Decode.string
            let! body = Decode.object.optional "body" Decode.text
            let! order = Decode.object.optional "order" Decode.float
            return
                { Author = author
                  Body = defaultArg body Text.empty
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
    let private draftsToDomain (h: HashMap<string, DraftFields>) : Map<PeerId, DraftState> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            // The key is the author (one draft per client); an invalid key is skipped so
            // the decode stays total over a doc shared with peers we don't control.
            match PeerId.create key with
            | Ok author -> acc |> Map.add author { Author = author; Body = f.Body }
            | Error _ -> acc)

    let private queueToDomain (h: HashMap<string, QueuedFields>) : Map<QueueId, QueuedMessage> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            match QueueId.create key, PeerId.create f.Author with
            | Ok id, Ok author ->
                acc |> Map.add id { QueueId = id; Author = author; Body = f.Body; Order = f.Order }
            | _ -> acc)

    /// Decode the synced state out of a doc. Total, and decode-empty = init: on an empty
    /// doc every optional comes back `None` and this returns `SyncedSessionState.empty`.
    let decode<'m> : Decoder<'m, SyncedSessionState> =
        Decode.object {
            let! drafts = Decode.object.optional "drafts" (Decode.map decodeDraft)
            let! queue = Decode.object.optional "queue" (Decode.map decodeQueued)
            let! brief = Decode.object.optional "sharedBrief" decodeBrief
            return
                { Drafts = drafts |> Option.map draftsToDomain |> Option.defaultValue Map.empty
                  Queue = queue |> Option.map queueToDomain |> Option.defaultValue Map.empty
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
