namespace Yession.SessionProcess

open Yession.Domain

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
///
/// Runtime-agnostic (works on both .NET and the single-threaded Fable/Node runtime): a
/// one-directional pipe is a queue plus at most one pending reader continuation, matching
/// the single-consumer `FrameChannel.Receive` contract.
module InMemoryChannel =

    /// A one-directional, single-consumer pipe of frames with an end-of-stream signal.
    type private Pipe<'State>() =
        let queue = System.Collections.Generic.Queue<SessionFrame<'State>>()
        let mutable closed = false
        let mutable pending : (SessionFrame<'State> option -> unit) option = None

        /// Hand the next item to a waiting reader, or buffer it.
        member _.Write(frame: SessionFrame<'State>) =
            match pending with
            | Some cont -> pending <- None; cont (Some frame)
            | None -> queue.Enqueue frame

        /// Signal end-of-stream; a waiting reader is completed with `None`.
        member _.Close() =
            if not closed then
                closed <- true
                match pending with
                | Some cont -> pending <- None; cont None
                | None -> ()

        /// Read the next frame, `None` once drained and closed.
        member _.Read() : Async<SessionFrame<'State> option> =
            Async.FromContinuations(fun (cont, _, _) ->
                if queue.Count > 0 then cont (Some(queue.Dequeue()))
                elif closed then cont None
                else pending <- Some cont)

    /// Create a connected (clientEnd, serverEnd) pair.
    let createPair<'State> () : FrameChannel<'State> * FrameChannel<'State> =
        let clientToServer = Pipe<'State>()
        let serverToClient = Pipe<'State>()

        let make (outbound: Pipe<'State>) (inbound: Pipe<'State>) : FrameChannel<'State> =
            { Send = fun frame -> async { outbound.Write frame }
              Receive = fun () -> inbound.Read()
              // Closing our outbound pipe signals the remote that we have left.
              Close = fun () -> async { outbound.Close() } }

        make clientToServer serverToClient, make serverToClient clientToServer
