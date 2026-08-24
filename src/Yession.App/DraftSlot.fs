namespace Yession.App

open Fable.Core
open Yjs
open Yession.Domain
open Yession.Domain.Collab

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

    /// Whether a body carries anything a send would snapshot.
    type BodyContent =
        | Empty
        | HasContent

    /// Whether the doc announces the peer's draft to its collaborators.
    type SlotState =
        | Published
        | Unpublished

    /// What a body and its slot disagree about, if anything.
    type Reconciliation =
        | Publish
        | Retract
        | Agreed

    /// What to do about a body and its slot. Distinct types for the two facts, so a call site
    /// cannot transpose them, and no wildcard, so the compiler is the one asserting every state is
    /// answered. Pure: publishing needs a fresh queue key, and minting is `settle`'s job.
    let reconcile (body: BodyContent) (slot: SlotState) : Reconciliation =
        match body, slot with
        | HasContent, Unpublished -> Publish
        | Empty, Published -> Retract
        | HasContent, Published -> Agreed
        | Empty, Unpublished -> Agreed

    /// A peer's body as the rule sees it. Emptiness is measured in Markdown — the same read a
    /// send snapshots and the drain durably records — so "publishes a slot" and "sends something"
    /// can never disagree. (Fragment length would be cheaper and wrong: mounting an editor writes
    /// an empty paragraph, and an empty paragraph is not a draft.)
    let contentOf (registry: BodyRegistry) (peer: PeerId) : BodyContent =
        if (Markdown.ofFragment (registry.Fragment (BodyKey.draft peer))).Trim () = "" then Empty
        else HasContent

    /// Whether the doc currently announces this peer's draft.
    let slotOf (doc: Y.Doc) (peer: PeerId) : SlotState =
        if SyncedStateSync.hasDraft doc peer then Published else Unpublished

    /// The queue key a newly published draft carries: minted once, by its author, and read by
    /// every co-editor that later sends it.
    let private mintQueueId () : QueueId =
        match QueueId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "queue id invariant violated: %s" e

    /// Bring the local peer's slot into agreement with its body, now. Both facts are read from
    /// the doc, so the rule never acts on a stale model snapshot.
    let settle (doc: Y.Doc) (registry: BodyRegistry) (peer: PeerId) (dispatch: Sink<ClientMsg>) : unit =
        match reconcile (contentOf registry peer) (slotOf doc peer) with
        | Publish -> dispatch (EnsureDraftMsg (peer, mintQueueId ()))
        | Retract -> dispatch (DiscardDraftMsg peer)
        | Agreed -> ()

    // The body is a `Y.XmlFragment`, so its own change events are the exact signal this rule
    // needs — `observeDeep` fires for a keystroke and for a merged remote edit, and for nothing
    // else. Watching the whole doc instead would re-run the rule (and a Markdown serialize) on
    // every title edit, queue edit, and peer's draft.
    [<Emit("$0.observeDeep($1)")>]
    let private observeDeep (fragment: Y.XmlFragment) (handler: unit -> unit) : unit = jsNative

    [<Emit("$0.unobserveDeep($1)")>]
    let private unobserveDeep (fragment: Y.XmlFragment) (handler: unit -> unit) : unit = jsNative

    /// Keep the local peer's slot in step with its own body: settle now, then on every change to
    /// that body. Only the local peer's slot — publication is the author's, exactly as sending is.
    /// The returned `Subscription` stops observing.
    ///
    /// Body changes are the only thing that can move the slot under this rule, so nothing else is
    /// watched. State that arrives another way — a persisted doc replaying at load, say — is
    /// answered by calling `settle` at that point, which is what the browser does once its
    /// IndexedDB store has synced.
    let follow (doc: Y.Doc) (registry: BodyRegistry) (peer: PeerId) (dispatch: Sink<ClientMsg>) : Subscription =
        let fragment = registry.Fragment (BodyKey.draft peer)
        let handler () = settle doc registry peer dispatch
        settle doc registry peer dispatch
        observeDeep fragment handler
        Subscription.ofStop (fun () -> unobserveDeep fragment handler)

/// The same publication rule for a terminal composer (Plan 13). Identical in shape and in
/// reasoning — a slot exists exactly while its command line has content — over a plain
/// `Y.Text` instead of a rich body, so emptiness is a string test rather than a Markdown
/// serialize.
module TerminalDraftSlot =

    [<Emit("$0.observe($1)")>]
    let private observe (text: Y.Text) (handler: unit -> unit) : unit = jsNative

    [<Emit("$0.unobserve($1)")>]
    let private unobserve (text: Y.Text) (handler: unit -> unit) : unit = jsNative

    [<Emit("$0.toString()")>]
    let private textString (text: Y.Text) : string = jsNative

    let private mintQueueId () : QueueId =
        match QueueId.create (string (System.Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "queue id invariant violated: %s" e

    /// Bring the local peer's slot in this terminal into agreement with its command line.
    /// Both facts are read from the doc, never from a model snapshot, so the rule cannot
    /// act on a stale pair.
    let settle
        (doc: Y.Doc)
        (registry: TextRegistry)
        (terminal: TerminalId)
        (peer: PeerId)
        (dispatch: Sink<ClientMsg>)
        : unit =
        let content =
            if (textString (registry.Text (BodyKey.terminalDraft terminal peer))).Trim () = ""
            then DraftSlot.Empty
            else DraftSlot.HasContent
        let slot =
            if SyncedStateSync.hasTerminalDraft doc terminal peer then DraftSlot.Published else DraftSlot.Unpublished
        match DraftSlot.reconcile content slot with
        | DraftSlot.Publish -> dispatch (EnsureTerminalDraftMsg (terminal, peer, mintQueueId ()))
        | DraftSlot.Retract -> dispatch (DiscardTerminalDraftMsg (terminal, peer))
        | DraftSlot.Agreed -> ()

    /// Keep the local peer's slot in step with its own command line, in one terminal.
    let follow
        (doc: Y.Doc)
        (registry: TextRegistry)
        (terminal: TerminalId)
        (peer: PeerId)
        (dispatch: Sink<ClientMsg>)
        : Subscription =
        let text = registry.Text (BodyKey.terminalDraft terminal peer)
        let handler () = settle doc registry terminal peer dispatch
        settle doc registry terminal peer dispatch
        observe text handler
        Subscription.ofStop (fun () -> unobserve text handler)
