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

    { Send = fun frame -> async { dc.sendMessage (Codec.toString frameCodec frame) |> ignore }
      Receive =
        fun () ->
            Async.FromContinuations(fun (cont, _, _) ->
                if queue.Count > 0 then cont (queue.Dequeue())
                elif closed then cont None
                else pending <- Some cont)
      Close = fun () -> async { dc.close () } }

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
        let! answerText = postText signalUrl offer |> Async.AwaitPromise
        let answer = parseSdp answerText
        pc.setRemoteDescription (answer.sdp, answer.``type``)
        do! opened
        return frameChannel dc
    }
