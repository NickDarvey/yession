namespace Yession.Domain.Link

open Yession.Domain

open System

/// Keeping a session link honest: the heartbeat that turns "this transport has silently
/// stopped carrying anything" into the one signal every consumer already handles — a closed
/// channel.
///
/// The gap this closes. A WebRTC data channel can die without saying so: a phone goes into
/// the background, a network switches from WiFi to cellular, a tunnel re-routes. The DTLS
/// association is gone but `readyState` still reads `open`, so sends are accepted into
/// nothing and no `close` event ever fires. Both pumps in this repo react correctly to a
/// channel that CLOSES (the client reconnects and re-pushes its full doc state; the Session
/// Process runs its cleanup, releasing terminal leases and clearing presence) — they simply
/// never learned that this one had. A message queued on a phone therefore sat on screen
/// until the page was reloaded.
///
/// `Ping`/`Pong` have been in `ControlFrame` since the protocol was written, constructed by
/// nothing and answered by nothing. This is what they were for.
///
/// Three properties are worth stating, because each one is load-bearing:
///
/// * **Death has exactly one expression.** A link that stops answering is CLOSED, and that is
///   all. Nothing above learns about liveness through a second channel, so there is no second
///   state to keep consistent with the first.
/// * **Any inbound frame is proof of life.** A busy link is never probed; only a quiet one
///   is. That keeps the mechanism free where it would cost the most — a terminal streaming
///   output, a turn writing a message — and it is why the tick can afford to be short.
/// * **A heartbeat is invisible above this module.** `Receive` answers an inbound `Ping` and
///   consumes it, and consumes a `Pong`, so neither pump ever sees one. That is not tidiness:
///   `PeerSession.run` rejects any first frame that is not `PeerHello`, and a refused peer
///   PARKS rather than retrying, so a probe racing ahead of the handshake would be fatal.
///   Wrapping both ends makes it unrepresentable.
module Link =

    /// What a supervisor did, for whoever is watching. A link that dies silently is a link
    /// whose death has to be inferred from its consequences, which is the debugging cost this
    /// whole module exists to stop paying.
    type LinkEvent =
        /// The link was quiet, so it was asked to prove itself. Carries how many consecutive
        /// quiet ticks have passed — the last one before death is the interesting one.
        | LinkProbed of quietTicks: int
        /// The link did not answer and has been closed.
        | LinkDied of reason: string

    /// How hard a link is held to account. The clock is a port for the reason every schedule
    /// in this repo takes one (`Resilience.Policy`): the cadence is fixed in code, the waiting
    /// is not, so a test asserts the whole rule in zero real time.
    type LinkPolicy =
        { /// How long a link may be quiet before it is probed.
          Tick : TimeSpan
          /// How many consecutive quiet ticks end it. The first probe goes out on tick one,
          /// so this is also "how many unanswered probes are tolerated, plus one".
          QuietTicksBeforeDeath : int
          Sleep : TimeSpan -> Async<unit>
          Observe : LinkEvent -> unit }

    module LinkPolicy =

        /// One second. Short on purpose: a probe is a handful of bytes on a channel that is
        /// already open, and the thing being bought is the difference between a session that
        /// heals itself and one that needs a reload.
        let tick = TimeSpan.FromSeconds 1.0

        /// Three, so a dead link is detected in about three seconds.
        ///
        /// The cost of being wrong in this direction is one reconnect: the client re-opens and
        /// re-pushes full doc state, which is idempotent, and the Session Process re-accepts a
        /// peer it just lost. The cost of being wrong in the other direction is a person
        /// staring at a message that will never send. They are not the same size.
        let quietTicksBeforeDeath = 3

        /// The shipped policy: real waiting, nothing watching.
        let shipped : LinkPolicy =
            { Tick = tick
              QuietTicksBeforeDeath = quietTicksBeforeDeath
              Sleep = Resilience.Policy.sleep
              Observe = ignore }

    /// Supervise a channel: same channel, now with a heartbeat behind it.
    ///
    /// The returned channel buffers, because the supervisor has to answer a probe whether or
    /// not anyone upstream is reading — the same one-internal-pump shape every other
    /// `FrameChannel` in this repo uses.
    ///
    /// On death it closes the underlying channel AND ends its own receive. Both, because they
    /// are the two ends of one fact and only one of them is anybody else's: `Close` tells the
    /// REMOTE we have gone (`InMemoryChannel.Close` closes only the outbound pipe, and a
    /// browser data channel is no different), while the local pump is left parked on a
    /// `Receive` that will never complete — which is precisely the freeze this module exists
    /// to end, reintroduced one layer up.
    let supervise (policy: LinkPolicy) (channel: FrameChannel<'State>) : FrameChannel<'State> =
        let queue = System.Collections.Generic.Queue<SessionFrame<'State> option>()
        let mutable pending : (SessionFrame<'State> option -> unit) option = None
        let mutable ended = false
        // Traffic since the last tick. Set by anything inbound — a probe's answer is not
        // special, it is just the frame a quiet link had to be asked for.
        let mutable heard = false

        let deliver (item: SessionFrame<'State> option) =
            match pending with
            | Some cont ->
                pending <- None
                cont item
            | None -> queue.Enqueue item

        /// End this end of the link, once. `ended` gates it so a supervisor deciding the link
        /// is dead and an underlying channel closing underneath it cannot both report it.
        let endLink () =
            if not ended then
                ended <- true
                deliver None

        // Read from the channel for as long as it lasts, answering probes and consuming
        // answers on the way past.
        let rec pump () =
            async {
                match! channel.Receive () with
                | None -> endLink ()
                | Some frame ->
                    heard <- true
                    match frame with
                    | Control Ping ->
                        do! channel.Send (Control Pong)
                        return! pump ()
                    | Control Pong -> return! pump ()
                    | _ ->
                        deliver (Some frame)
                        return! pump ()
            }

        // Ask a quiet link to prove itself, and give up on one that will not. Stops within a
        // tick of the link ending, whichever end ended it.
        let rec beat (quiet: int) =
            async {
                do! policy.Sleep policy.Tick
                if ended then return ()
                elif heard then
                    heard <- false
                    return! beat 0
                else
                    let quiet = quiet + 1
                    if quiet >= policy.QuietTicksBeforeDeath then
                        policy.Observe (
                            LinkDied (sprintf "no answer in %d ticks" quiet))
                        endLink ()
                        do! channel.Close ()
                    else
                        policy.Observe (LinkProbed quiet)
                        do! channel.Send (Control Ping)
                        return! beat quiet
            }

        Async.StartImmediate (pump ())
        Async.StartImmediate (beat 0)

        { Send = channel.Send
          Receive =
            fun () ->
                Async.FromContinuations(fun (cont, _, _) ->
                    if queue.Count > 0 then cont (queue.Dequeue ())
                    elif ended then cont None
                    else pending <- Some cont)
          Close =
            fun () ->
                async {
                    endLink ()
                    do! channel.Close ()
                } }
