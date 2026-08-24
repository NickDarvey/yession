namespace Yession.SessionProcess

open Yession.Domain
open Yession.Domain.Link

/// The Session Process side of a single peer connection: the token-gated hello/accept
/// handshake, presence events, and the receive pump. `State` frames are handed to the
/// injected handlers (the sync boundary, Step 05); Command/EventLog handlers arrive in
/// later steps and are still drained.
module PeerSession =

    /// The per-peer hooks the composition root injects into a session's pump.
    type PeerHandlers<'State> =
        { /// Called with every `State` frame payload the accepted peer sends.
          OnState : 'State -> unit
          /// Called with every command request the accepted peer sends; the result is
          /// returned to the peer as the command response.
          OnCommand : PeerId -> SessionCommand -> Async<SessionCommandResult>
          /// Called with every ephemeral presence update the peer sends (relay to other
          /// peers); never durable, so nothing is appended.
          OnPresence : PresencePayload -> unit
          /// Called with every terminal frame the peer sends (Plan 13, stage 2e): keystrokes
          /// and viewport resizes for a terminal it believes it holds. Answered by no response
          /// frame at all, deliberately — a keystroke that needed an acknowledgement would make
          /// typing a round trip, and the authority on whether it lands is the lease, which
          /// only the Process can check. A frame from a non-holder is dropped there.
          OnTerminal : PeerId -> TerminalFrame -> unit
          /// Called once after `PeerAccepted` is sent, with the peer's channel (e.g. to
          /// send the initial state and register the peer for relay). Returns the
          /// cleanup to run when the peer disconnects.
          OnAccepted : PeerId -> FrameChannel<'State> -> Async<unit -> unit> }

    module PeerHandlers =

        /// No state or command handling: frames are drained (commands rejected),
        /// nothing is set up or torn down.
        let none<'State> : PeerHandlers<'State> =
            { OnState = ignore
              OnCommand = fun _ _ -> async { return CommandRejected "commands are not handled" }
              OnPresence = ignore
              OnTerminal = fun _ _ -> ()
              OnAccepted = fun _ _ -> async { return fun () -> () } }

    /// Run a peer session over a connected channel until the peer disconnects.
    ///
    /// - On a `PeerHello` whose token `attributionOf` recognises (the composition root
    ///   injects the check — the session-minted peer-token store in the product): append
    ///   `PeerJoined` carrying the token's attribution (the Manager-verified user, when
    ///   one exists — never anything the peer asserted), reply with `PeerAccepted`, run
    ///   `handlers.OnAccepted`, pump frames until the channel closes (`State` payloads
    ///   go to `handlers.OnState`), run the cleanup, then append `PeerLeft`.
    /// - On an invalid token or an unexpected first frame: reply `PeerRejected` and close.
    ///
    /// Side effects (appends) go through the injected `EventLog`, honouring "the Session
    /// Process is the only event writer".
    let run
        (sessionId: SessionId)
        (attributionOf: string -> PeerAttribution option)
        (log: EventLog<SessionEvent>)
        (handlers: PeerHandlers<'State>)
        (channel: FrameChannel<'State>)
        : Async<unit> =
        async {
            let! first = channel.Receive ()
            match first |> Option.map (fun f -> f, (match f with Control (PeerHello h) -> attributionOf h.Token | _ -> None)) with
            | Some (Control (PeerHello hello), Some attribution) ->
                let actor = PeerRef hello.PeerId
                let! joined =
                    log.Append
                        actor
                        (PeerJoined
                            { PeerId = hello.PeerId
                              DisplayName = hello.DisplayName
                              User = PeerAttribution.userOf attribution })
                let accepted =
                    { SessionId = sessionId
                      AssignedDisplayName = hello.DisplayName
                      LatestOffset = Some joined.Offset }
                do! channel.Send (Control (PeerAccepted accepted))
                let! cleanup = handlers.OnAccepted hello.PeerId channel

                let rec pump () =
                    async {
                        match! channel.Receive () with
                        | Some (State (StateSync payload)) ->
                            handlers.OnState payload
                            return! pump ()
                        | Some (Command (Request (requestId, command))) ->
                            let! result = handlers.OnCommand hello.PeerId command
                            do! channel.Send (Command (Response (requestId, result)))
                            return! pump ()
                        | Some (Presence payload) ->
                            handlers.OnPresence payload
                            return! pump ()
                        | Some (Terminal frame) ->
                            handlers.OnTerminal hello.PeerId frame
                            return! pump ()
                        | Some (EventLog (ReadEventsAfter (requestId, after, limit))) ->
                            // Clients are read-only consumers: reads are served, and no
                            // frame exists that appends — peers cannot write the log.
                            let! page = log.Read after limit
                            do! channel.Send (EventLog (EventsPage (requestId, page)))
                            return! pump ()
                        | Some _ ->
                            // Anything else a peer sends (e.g. spoofed pages or
                            // availability hints) is drained without effect.
                            return! pump ()
                        | None -> return ()
                    }
                do! pump ()

                cleanup ()
                let! _ = log.Append actor (PeerLeft { PeerId = hello.PeerId })
                return ()
            | Some (Control (PeerHello _), None) ->
                do! channel.Send (Control (PeerRejected "invalid peer token"))
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
