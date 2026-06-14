module Yession.Host.Tests.E2E

// End-to-end verification of the real WebRTC transport: starts a Session Process and a
// client peer in one Node process, connects them over an actual libdatachannel data
// channel, and checks the token-gated handshake plus presence events.
//
// Everything is event-driven — the test awaits ICE gathering completion, the data-channel
// `open` event, frame receipt, and an eagerly-registered "session ended" signal. There
// are no sleeps or polling, so the result does not depend on timing.

open System
open Yession.Domain
open Yession.SessionProcess
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private fail (msg: string) : 'a =
    eprintfn "E2E FAIL: %s" msg
    Interop.cleanup ()
    Interop.exit 1
    failwith msg

let private check (cond: bool) (msg: string) =
    if not cond then fail msg |> ignore

let private eventsOf (host: Host.SessionHost) : Async<SessionEvent list> =
    async {
        let! page = host.Log.Read None Int32.MaxValue
        return page.Events |> List.map (fun e -> e.Event)
    }

let private run () =
    async {
        let token = "e2e-token"
        let port = 8099
        let sessionId = SessionId.create "e2e-session" |> expect
        let peerId = PeerId.create "ada" |> expect
        let joined = PeerJoined { PeerId = peerId; DisplayName = "Ada" }
        let left = PeerLeft { PeerId = peerId }

        let! host = Host.start sessionId token port
        let signalUrl = sprintf "http://127.0.0.1:%d/signal" port

        // --- valid handshake completes over a real data channel ----------------
        let! channel = WebRtc.connect signalUrl
        do! channel.Send (Control (PeerHello { PeerId = peerId; DisplayName = "Ada"; Token = token }))
        let! accepted = channel.Receive ()
        match accepted with
        | Some (Control (PeerAccepted a)) ->
            check (a.SessionId = sessionId) "accepted session id mismatch"
            check (a.AssignedDisplayName = "Ada") "assigned display name mismatch"
            check (a.LatestOffset = Some EventOffset.zero) "latest offset should be the PeerJoined offset (0)"
        | other -> fail (sprintf "expected PeerAccepted, got %A" other)

        let! afterJoin = eventsOf host
        check (afterJoin = [ joined ]) "log should contain only PeerJoined after the handshake"

        // disconnect -> PeerLeft, observed via the session-end signal (no timing)
        let sessionEnded = host.WaitForNextSessionEnd ()
        do! channel.Close ()
        do! sessionEnded
        let! afterLeave = eventsOf host
        check (afterLeave = [ joined; left ]) "log should be PeerJoined then PeerLeft after disconnect"

        // --- a bad token is rejected and appends nothing -----------------------
        let rejectedEnded = host.WaitForNextSessionEnd ()
        let! badChannel = WebRtc.connect signalUrl
        do! badChannel.Send (Control (PeerHello { PeerId = peerId; DisplayName = "Mallory"; Token = "wrong-token" }))
        let! rejected = badChannel.Receive ()
        match rejected with
        | Some (Control (PeerRejected _)) -> ()
        | other -> fail (sprintf "expected PeerRejected, got %A" other)
        do! rejectedEnded
        let! finalEvents = eventsOf host
        check (finalEvents = [ joined; left ]) "a rejected peer must not append events"

        printfn "E2E PASS: real WebRTC handshake, presence (PeerJoined/PeerLeft), and token rejection verified"
        do! host.Stop ()
        Interop.cleanup ()
        Interop.exit 0
    }

Async.StartImmediate(
    async {
        try
            do! run ()
        with ex ->
            eprintfn "E2E FAIL (exception): %s" ex.Message
            Interop.cleanup ()
            Interop.exit 1
    })
