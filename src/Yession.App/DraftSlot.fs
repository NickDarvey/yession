namespace Yession.App

open Yjs
open Yession.Domain

/// The draft-slot publication rule: a peer's slot is in the synced state IFF that peer's body
/// has content.
///
/// The slot is what every OTHER client renders a draft box from, so publishing one when the
/// composer mounted — what the browser used to do — put an empty box on every peer's composer
/// for everyone who had ever opened the session, and left the slot in the doc forever after they
/// left. Publication follows the body instead: the first keystroke materialises the slot,
/// emptying the body retracts it.
///
/// Nothing else has to change, because the body is NOT inside the slot: it is a top-level
/// fragment root (RichText.fs) that exists independently, so the local composer still mounts and
/// types before any slot exists — and a send, which removes the slot, still carries the body over.
module DraftSlot =

    /// The message that reconciles a slot with its body, or `None` when they already agree.
    let reconcile (peer: PeerId) (hasContent: bool) (hasSlot: bool) : ClientMsg option =
        match hasContent, hasSlot with
        | true, false -> Some (EnsureDraftMsg peer)
        | false, true -> Some (DiscardDraftMsg peer)
        | _ -> None

    /// Whether a peer's body carries anything a send would snapshot. Measured in Markdown, not
    /// fragment length: mounting an editor writes an empty paragraph into the fragment, and an
    /// empty paragraph is not a draft.
    let hasContent (registry: BodyRegistry) (peer: PeerId) : bool =
        (Markdown.ofFragment (registry.Fragment (BodyKey.draft peer))).Trim () <> ""

    /// Keep the local peer's slot in step with its own body for as long as the doc lives:
    /// reconcile now, then after every doc update (a keystroke, a remote merge, a persistence
    /// replay). Both bits are read from the doc, so the rule never acts on a stale model.
    /// Only the local peer's slot: publication is the author's, exactly as sending is.
    let follow (doc: Y.Doc) (registry: BodyRegistry) (peer: PeerId) (dispatch: ClientMsg -> unit) : unit =
        let settle () =
            reconcile peer (hasContent registry peer) (SyncedStateSync.hasDraft doc peer)
            |> Option.iter dispatch
        settle ()
        DocSync.onAnyUpdate doc settle
