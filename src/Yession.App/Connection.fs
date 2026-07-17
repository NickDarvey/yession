namespace Yession.App

open Yession.Domain

/// Drives the client side of the session transport over a connected `FrameChannel`:
/// performs the token-gated hello/accept handshake and pumps inbound frames into
/// `ClientMsg` values, so the Elmish model reflects connection-state transitions. The
/// channel is supplied by the WebRTC adapter (or an in-memory pair in tests), keeping
/// this logic free of Node/WebRTC IO. See docs/plans/00-init/04-*.
module Connection =

    /// Run the client connection until the channel closes.
    ///
    /// Sends `PeerHello`, then dispatches: `ConnectingMsg` immediately, `ConnectedMsg` on
    /// `PeerAccepted`, `RejectedMsg` on `PeerRejected` (and stops), `EventsAvailableMsg`
    /// when the Session Process advertises a new latest offset, and `DisconnectedMsg`
    /// when the remote end closes the channel. `State` frame payloads are handed to
    /// `onState` (the sync boundary applies them to the local Yjs doc); command
    /// responses go to `onResponse` and event pages to `onEventsPage` (the driver
    /// correlates both to their requests).
    let run
        (hello: PeerHelloPayload)
        (dispatch: ClientMsg -> unit)
        (onState: 'State -> unit)
        (onResponse: RequestId -> SessionCommandResult -> unit)
        (onEventsPage: RequestId -> EventPage<SessionEvent> -> unit)
        (channel: FrameChannel<'State>)
        : Async<unit> =
        async {
            dispatch ConnectingMsg
            do! channel.Send (Control (PeerHello hello))

            let rec pump () =
                async {
                    match! channel.Receive () with
                    | Some (Control (PeerAccepted accepted)) ->
                        dispatch (ConnectedMsg accepted)
                        return! pump ()
                    | Some (Control (PeerRejected reason)) ->
                        dispatch (RejectedMsg reason)
                        return ()
                    | Some (EventLog (EventsAvailable latest)) ->
                        dispatch (EventsAvailableMsg latest)
                        return! pump ()
                    | Some (State (StateSync payload)) ->
                        onState payload
                        return! pump ()
                    | Some (Command (Response (requestId, result))) ->
                        onResponse requestId result
                        return! pump ()
                    | Some (EventLog (EventsPage (requestId, page))) ->
                        onEventsPage requestId page
                        return! pump ()
                    | Some (Presence payload) ->
                        // A collaborator's caret moved (or cleared) in the title.
                        dispatch (RemotePresenceMsg payload)
                        return! pump ()
                    | Some _ ->
                        // Anything else (e.g. inbound command requests) is not part of
                        // the client's protocol surface and is drained.
                        return! pump ()
                    | None ->
                        dispatch DisconnectedMsg
                        return ()
                }

            do! pump ()
        }
