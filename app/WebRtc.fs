module Yession.Host.WebRtc

// The WebRTC carrier for the session transport: a `FrameChannel` over a libdatachannel
// data channel, plus the offer/answer signalling primitives. The connection is
// established with a non-trickle exchange — each side waits for ICE gathering to complete
// (an event), then exchanges a single complete SDP — so there are no candidate-timing
// races and nothing depends on sleeps. See docs/design.md §2.3.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.SessionProcess
open Yession.Host.Interop

type [<AllowNullLiteral>] private SdpMessage =
    abstract ``type`` : string
    abstract sdp : string

[<Emit("JSON.stringify({ type: $0, sdp: $1 })")>]
let private sdpToJson (ty: string) (sdp: string) : string = jsNative

[<Emit("JSON.parse($0)")>]
let private parseSdp (json: string) : SdpMessage = jsNative

/// The transport never inspects the state-sync payload, so its codec is just a string.
let private frameCodec : Codec<SessionFrame<string>> = Codec.sessionFrame Codec.string

/// Bridge a push-based data channel into the pull-based `FrameChannel.Receive`.
/// A single consumer (the peer-session pump) is assumed.
let frameChannel (dc: DataChannel) : FrameChannel<string> =
    let queue = System.Collections.Generic.Queue<SessionFrame<string> option>()
    let mutable pending : (SessionFrame<string> option -> unit) option = None
    let mutable closed = false

    let deliver (item: SessionFrame<string> option) =
        match pending with
        | Some cont ->
            pending <- None
            cont item
        | None -> queue.Enqueue item

    dc.onMessage (fun (msg: string) ->
        match Codec.fromString frameCodec msg with
        | Ok frame -> deliver (Some frame)
        | Error e -> JS.console.error ("frame decode failed: " + e))

    dc.onClosed (fun () ->
        if not closed then
            closed <- true
            deliver None)

    { Send =
        fun frame ->
            async {
                // A peer can vanish between frames (e.g. presence broadcasts racing a
                // disconnect); sending into a closed channel must be a no-op. The `isOpen`
                // guard narrows the window but can't close it: libdatachannel runs its own
                // threads, so the channel can transition to closed between this check and the
                // native send, making `send()` throw. With the addon sharing the process C++
                // runtime (see nix/node-datachannel.nix) that throw is an ordinary catchable
                // JS error, so we swallow it here — a lost frame on a dying channel is a no-op.
                if not closed && dc.isOpen () then
                    try dc.sendMessage (Codec.toString frameCodec frame) |> ignore
                    with _ -> ()
            }
      Receive =
        fun () ->
            Async.FromContinuations(fun (cont, _, _) ->
                if queue.Count > 0 then cont (queue.Dequeue())
                elif closed then cont None
                else pending <- Some cont)
      Close = fun () -> async { dc.close () } }

/// Close a peer connection and resolve once libdatachannel reports it CLOSED — the
/// library's own signal that its threads are finished with the object. This is the
/// deterministic teardown primitive: after it resolves, a global `Interop.cleanup ()`
/// cannot race a callback into this connection. The waiter registers eagerly (the
/// state callback is a single slot, and nothing else claims it on these connections —
/// gathering uses the separate onGatheringStateChange slot).
let closePeerConnection (pc: PeerConnection) : Async<unit> =
    let mutable closed = false
    let mutable waiter : (unit -> unit) option = None
    pc.onStateChange (fun state ->
        if state = "closed" && not closed then
            closed <- true
            match waiter with
            | Some w -> waiter <- None; w ()
            | None -> ())
    if pc.state () = "closed" then
        async { () }
    else
        async {
            pc.close ()
            return!
                Async.FromContinuations (fun (cont, _, _) ->
                    if closed then cont () else waiter <- Some cont)
        }

/// Await the data channel `open` event. Registers the callback eagerly (at call time) so
/// the event cannot be missed between construction and awaiting.
let private onceOpen (dc: DataChannel) : Async<unit> =
    let mutable opened = false
    let mutable waiter : (unit -> unit) option = None
    dc.onOpen (fun () ->
        opened <- true
        match waiter with
        | Some w -> waiter <- None; w ()
        | None -> ())
    async {
        return!
            Async.FromContinuations(fun (cont, _, _) ->
                if opened then cont () else waiter <- Some cont)
    }

/// Resolve with the complete local SDP (JSON) once ICE gathering finishes. Registers the
/// gathering callback eagerly (at call time), so it must be created *before* the action
/// that starts negotiation (creating the data channel, or setting the remote offer).
/// Non-trickle: the gathered `localDescription()` already embeds all candidates.
let private gatherDescription (pc: PeerConnection) : Async<string> =
    let mutable result : string option = None
    let mutable waiter : (string -> unit) option = None
    pc.onGatheringStateChange (fun state ->
        if state = "complete" && Option.isNone result then
            let ld = pc.localDescription ()
            let json = sdpToJson ld.``type`` ld.sdp
            result <- Some json
            match waiter with
            | Some w -> waiter <- None; w json
            | None -> ())
    async {
        return!
            Async.FromContinuations(fun (cont, _, _) ->
                match result with
                | Some json -> cont json
                | None -> waiter <- Some cont)
    }

/// Server side: apply a remote offer and resolve with the answer SDP (JSON). Auto-
/// negotiation generates the answer automatically when the remote description is set.
let answerOffer (pc: PeerConnection) (offerSdp: string) : Async<string> =
    async {
        let answerReady = gatherDescription pc
        pc.setRemoteDescription (offerSdp, "offer")
        return! answerReady
    }

/// Client side: connect to a Session Process by posting an offer to its signalling URL,
/// applying the returned answer, and resolving once the data channel is open. Auto-
/// negotiation generates the offer automatically when the data channel is created.
let connect (signalUrl: string) : Async<FrameChannel<string>> =
    async {
        let pc = createPeerConnection "yession-client"
        let offerReady = gatherDescription pc
        let dc = pc.createDataChannel "session"
        let opened = onceOpen dc
        let! offer = offerReady
        let! answerText = postText signalUrl offer |> Interop.awaitPromise
        let answer = parseSdp answerText
        pc.setRemoteDescription (answer.sdp, answer.``type``)
        do! opened
        // The client owns this side's PeerConnection: closing the channel also closes
        // the connection and WAITS for libdatachannel to report it closed, so a caller
        // that has awaited `Close` may safely reach `Interop.cleanup ()` with no live
        // native objects behind it (deterministic — no sleeps).
        let channel = frameChannel dc
        let close () =
            async {
                do! channel.Close ()
                do! closePeerConnection pc
            }
        return { channel with Close = close }
    }
