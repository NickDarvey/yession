namespace Yession.Domain

// The Ylmish sync boundary (Step 05). `SyncedSessionState` is the only state that
// crosses it: the codec below names exactly the fields that sync and how each merges
// (docs/design.md §1 "Ylmish is the sync boundary"). The conversation projection is
// deliberately never mentioned, so it can never enter the Yjs document. `DocSync` is
// the transport adapter that moves Yjs updates over the opaque `State` frame.

open FSharp.Data.Adaptive
open Ylmish
open Ylmish.Codec

/// The adaptive companion of `SyncedSessionState`, hand-written in place of Adaptify
/// codegen (the model is small and Adaptify would add a build step). Drafts are keyed
/// by their app-minted `DraftId` so concurrent — even offline — creation is safe:
/// different keys never conflict.
type AdaptiveSyncedState =
    { Drafts : cmap<string, DraftState>
      SharedBrief : cval<SharedBrief option> }

module SyncedStateSync =

    let private draftsByKey (m: SyncedSessionState) : HashMap<string, DraftState> =
        m.Drafts |> Map.toSeq |> Seq.map (fun (k, v) -> DraftId.value k, v) |> HashMap.ofSeq

    /// `Create` for Ylmish's options: build the adaptive companion from a model.
    let create (m: SyncedSessionState) : AdaptiveSyncedState =
        { Drafts = cmap (draftsByKey m)
          SharedBrief = cval m.SharedBrief }

    /// `Update` for Ylmish's options: fold the next model into the companion. Setting
    /// `cmap.Value` yields keyed deltas, so only changed drafts re-encode.
    let update (a: AdaptiveSyncedState) (m: SyncedSessionState) : unit =
        a.Drafts.Value <- draftsByKey m
        a.SharedBrief.Value <- m.SharedBrief

    let private statusKey =
        function
        | Active -> "active"
        | Sending -> "sending"
        | Sent -> "sent"

    /// Total on purpose: an unknown status written by a newer schema reads as `Active`
    /// rather than failing the whole decode.
    let private statusOf =
        function
        | "sending" -> Sending
        | "sent" -> Sent
        | _ -> Active

    /// Per-draft encoding: author/status are honest LWW registers, the body is
    /// collaborative text — concurrent edits to the same body interleave and merge.
    let private encodeDraft (d: DraftState) : Encoded =
        Encode.object
            [ "author", Encode.string (AVal.constant (PeerId.value d.Author))
              "body", Encode.text (AVal.constant d.Body)
              "status", Encode.string (AVal.constant (statusKey d.Status)) ]

    let private encodeBrief (b: aval<SharedBrief>) : Encoded =
        Encode.object [ "body", Encode.string (b |> AVal.map (fun x -> x.Body)) ]

    /// Which parts of the session sync, and how each merges. Everything else in the
    /// models — the conversation projection above all — is app-only by omission.
    let encode (a: AdaptiveSyncedState) : Encoded =
        Encode.object
            [ "drafts", Encode.map encodeDraft (a.Drafts :> amap<_, _>)
              "sharedBrief", Encode.option encodeBrief a.SharedBrief ]

    /// The doc-side field shapes, before identifier validation.
    type private DraftFields =
        { Author : string
          Body : Text
          Status : string }

    let private decodeDraft<'m> : Decoder<'m, DraftFields> =
        Decode.object {
            let! author = Decode.object.required "author" Decode.string
            let! body = Decode.object.optional "body" Decode.text
            let! status = Decode.object.optional "status" Decode.string
            return
                { Author = author
                  Body = defaultArg body Text.empty
                  Status = defaultArg status "active" }
        }

    let private decodeBrief<'m> : Decoder<'m, SharedBrief> =
        Decode.object {
            let! body = Decode.object.required "body" Decode.string
            return { SharedBrief.Body = body }
        }

    /// Entries whose identifiers fail the smart constructors are skipped rather than
    /// failing the decode: the doc is shared with peers we don't control, and a decode
    /// must stay total.
    let private toDomain (h: HashMap<string, DraftFields>) : Map<DraftId, DraftState> =
        (Map.empty, HashMap.toSeq h)
        ||> Seq.fold (fun acc (key, f) ->
            match DraftId.create key, PeerId.create f.Author with
            | Ok id, Ok author ->
                acc |> Map.add id { DraftId = id; Author = author; Body = f.Body; Status = statusOf f.Status }
            | _ -> acc)

    /// Decode the synced state out of a doc. Total, and decode-empty = init: on an empty
    /// doc every optional comes back `None` and this returns `SyncedSessionState.empty`.
    let decode<'m> : Decoder<'m, SyncedSessionState> =
        Decode.object {
            let! drafts = Decode.object.optional "drafts" (Decode.map decodeDraft)
            let! brief = Decode.object.optional "sharedBrief" decodeBrief
            return
                { Drafts = drafts |> Option.map toDomain |> Option.defaultValue Map.empty
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
        if shareHas doc "sharedBrief" then (doc.getMap "sharedBrief" : Yjs.Y.Map<obj>) |> ignore

    /// Read the synced state currently in a doc (the decode direction alone — used by the
    /// Session Process, which observes the doc without running its own Ylmish binding yet).
    let ofDoc (doc: Yjs.Y.Doc) : Result<SyncedSessionState, Error list> =
        materializeRoots doc
        Decode.run SyncedSessionState.empty decode doc

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
