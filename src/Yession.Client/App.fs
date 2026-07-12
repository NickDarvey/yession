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
          /// Send a draft: the status moves to `Sending` locally and a `SendDraft`
          /// command goes to the Session Process; the response comes back as
          /// `DraftSendAcceptedMsg` / `DraftSendRejectedMsg`.
          SendDraft : DraftId -> unit }

    /// Wire a connected channel to the client's doc: locally-originated doc updates (the
    /// Ylmish binding's writes) are sent as `State` frames, inbound `State` payloads are
    /// applied to the doc, command responses are correlated back to their drafts, and
    /// the handshake/lifecycle frames drive `dispatch`. The doc listener is registered
    /// before the pump starts so no local update can be missed.
    let connect
        (doc: Y.Doc)
        (hello: PeerHelloPayload)
        (dispatch: ClientMsg -> unit)
        (channel: FrameChannel<string>)
        : Connection =
        DocSync.onLocalUpdate doc (fun payload ->
            Async.StartImmediate (channel.Send (State (StateSync payload))))

        // In-flight SendDraft requests, correlated by request id.
        let mutable pending : Map<RequestId, DraftId> = Map.empty
        let onResponse (requestId: RequestId) (result: SessionCommandResult) =
            match Map.tryFind requestId pending with
            | Some draftId ->
                pending <- Map.remove requestId pending
                match result with
                | CommandAccepted -> dispatch (DraftSendAcceptedMsg draftId)
                | CommandRejected reason -> dispatch (DraftSendRejectedMsg (draftId, reason))
            | None -> ()

        { Run = Connection.run hello dispatch (DocSync.applyRemote doc) onResponse channel
          SendDraft =
            fun draftId ->
                dispatch (SendDraftMsg draftId)
                let requestId = RequestId.fresh ()
                pending <- Map.add requestId draftId pending
                Async.StartImmediate (channel.Send (Command (Request (requestId, SendDraft draftId)))) }
