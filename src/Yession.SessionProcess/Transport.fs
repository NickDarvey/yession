namespace Yession.SessionProcess

open System.Threading.Channels
open Yession.Domain

/// An abstract, bidirectional channel of session frames. The real carrier is a WebRTC
/// data channel (with HTTP used only for static bootstrap and temporary signalling); this
/// abstraction lets the handshake and frame-pump logic be written and tested without the
/// Node/WebRTC IO. The WebRTC adapter is a thin shell that implements this interface.
/// See docs/design.md §2.3 and docs/plans/00-init/03-*.
///
/// `Receive` returns `None` once the channel is closed by the remote end.
type FrameChannel<'State> =
    { Send : SessionFrame<'State> -> Async<unit>
      Receive : unit -> Async<SessionFrame<'State> option>
      Close : unit -> Async<unit> }

/// The Session Process side of a single peer connection: the token-gated hello/accept
/// handshake, presence events, and the receive pump. Frame *handlers* for State/Command/
/// EventLog frames arrive in later steps; here they are simply drained.
module PeerSession =

    /// Run a peer session over a connected channel until the peer disconnects.
    ///
    /// - On a valid `PeerHello` (matching `token`): append `PeerJoined`, reply with
    ///   `PeerAccepted`, drain frames until the channel closes, then append `PeerLeft`.
    /// - On an invalid token or an unexpected first frame: reply `PeerRejected` and close.
    ///
    /// Side effects (appends) go through the injected `EventLog`, honouring "the Session
    /// Process is the only event writer".
    let run
        (sessionId: SessionId)
        (token: string)
        (log: EventLog<SessionEvent>)
        (channel: FrameChannel<'State>)
        : Async<unit> =
        async {
            let! first = channel.Receive ()
            match first with
            | Some (Control (PeerHello hello)) when hello.Token = token ->
                let actor = HumanPeer hello.PeerId
                let! joined =
                    log.Append actor (PeerJoined { PeerId = hello.PeerId; DisplayName = hello.DisplayName })
                let accepted =
                    { SessionId = sessionId
                      AssignedDisplayName = hello.DisplayName
                      LatestOffset = Some joined.Offset }
                do! channel.Send (Control (PeerAccepted accepted))

                // Drain frames until the peer disconnects. Real handlers land in later steps.
                let rec pump () =
                    async {
                        match! channel.Receive () with
                        | Some _ -> return! pump ()
                        | None -> return ()
                    }
                do! pump ()

                let! _ = log.Append actor (PeerLeft { PeerId = hello.PeerId })
                return ()
            | Some (Control (PeerHello _)) ->
                do! channel.Send (Control (PeerRejected "invalid session token"))
                do! channel.Close ()
            | _ ->
                do! channel.Send (Control (PeerRejected "expected PeerHello as first frame"))
                do! channel.Close ()
        }

/// An in-memory, fully connected pair of frame channels. Used to exercise the handshake
/// and presence logic deterministically without WebRTC. The two ends behave like a
/// loopback transport: frames written to one end are read from the other.
module InMemoryChannel =

    /// Create a connected (clientEnd, serverEnd) pair.
    let createPair<'State> () : FrameChannel<'State> * FrameChannel<'State> =
        let toServer = Channel.CreateUnbounded<SessionFrame<'State>>()
        let toClient = Channel.CreateUnbounded<SessionFrame<'State>>()

        let make (outbound: Channel<SessionFrame<'State>>) (inbound: Channel<SessionFrame<'State>>) : FrameChannel<'State> =
            { Send =
                fun frame ->
                    async {
                        // Sending on a closed channel is a no-op rather than an error.
                        outbound.Writer.TryWrite frame |> ignore
                    }
              Receive =
                fun () ->
                    async {
                        // WaitToReadAsync returns false once the channel is completed and
                        // drained, so the closed case is signalled without exceptions.
                        let! canRead = inbound.Reader.WaitToReadAsync().AsTask() |> Async.AwaitTask
                        if canRead then
                            match inbound.Reader.TryRead() with
                            | true, frame -> return Some frame
                            | false, _ -> return None
                        else
                            return None
                    }
              Close =
                fun () ->
                    async {
                        // Completing the outbound side signals the remote that we have left.
                        outbound.Writer.TryComplete() |> ignore
                    } }

        make toServer toClient, make toClient toServer
