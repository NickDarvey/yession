module Yession.Tests.Client

// End-to-end verification of the Browser Client shell against a real Session Process: the
// client connects over an actual WebRTC data channel, completes the token-gated
// handshake, and its Elmish model reflects the connection-state transitions and the
// offset / catch-up indicators the product requires. A dropped channel moves the model to
// `Reconnecting`.
//
// Event-driven throughout: the model is observed via predicate "waiters" that resolve the
// moment a dispatched message satisfies them — no sleeps or polling.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Link
open Yession.SessionProcess
open Yession.App
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private port = 8100
let private sessionId = SessionId.create "client-e2e-session" |> expect
let private peerId = PeerId.create "grace" |> expect
let private signalUrl = sprintf "http://127.0.0.1:%d/signal" port

let mutable private host : Host.SessionHost option = None

/// Build an observer that resolves a returned async the first time a dispatched model
/// satisfies `predicate`. `check` must be called on every model update.
let private waiterFor (predicate: ClientModel -> bool) : (ClientModel -> unit) * Async<unit> =
    let mutable fired = false
    let mutable cont : (unit -> unit) option = None
    let check (model: ClientModel) =
        if not fired && predicate model then
            fired <- true
            match cont with
            | Some c -> cont <- None; c ()
            | None -> ()
    let await =
        Async.FromContinuations(fun (c, _, _) ->
            if fired then c () else cont <- Some c)
    check, await

let tests =
    testList "Client shell E2E" [
        testCaseAsync "start the Session Process host" <|
            async {
                let! h = Host.start sessionId port
                host <- Some h
            }

        testCaseAsync "client connects, reaches Connected, renders offset + catch-up indicators" <|
            async {
                let mutable model = ClientModel.init { PeerId = peerId; DisplayName = "Grace" }
                let checkConnected, connected = waiterFor (fun m -> m.Connection = Connected)
                let checkReconnecting, reconnecting = waiterFor (fun m -> m.Connection = Reconnecting)
                let dispatch msg =
                    model <- ClientModel.update msg model
                    checkConnected model
                    checkReconnecting model

                let! channel = WebRtc.connect signalUrl
                let hello = { PeerId = peerId; DisplayName = "Grace"; Token = host.Value.MintPeerToken () }
                Async.StartImmediate(Connection.run hello dispatch ignore (fun _ _ -> ()) (fun _ _ -> ()) (fun _ _ -> ()) channel)

                // Handshake completes -> Connected, with the joined offset as latest-known.
                do! connected
                Expect.equal model.Connection Connected "model reaches Connected"
                Expect.equal model.Peer.DisplayName "Grace" "assigned display name reflected"
                Expect.equal model.EventConsumer.LatestKnownOffset (Some EventOffset.zero) "latest-known offset = joined offset 0"
                Expect.equal model.EventConsumer.LastProcessedOffset None "nothing consumed yet"
                Expect.isTrue model.EventConsumer.IsCatchingUp "catch-up active while behind the known offset"

                // The UI renders both offset displays and the catch-up indicator — once the
                // catch-up has lasted long enough to be worth reporting, which is the only
                // state that shows them (a catch-up too brief to wait on is silent, so that
                // sending a message does not flicker the status).
                let html = Support.render (ClientModel.update (CatchUpSlowMsg true) model)
                Expect.isTrue (html.Contains Dom.Hooks.lastProcessedOffset) "last-processed offset display rendered"
                Expect.isTrue (html.Contains Dom.Hooks.latestKnownOffset) "latest-known offset display rendered"
                Expect.isTrue (html.Contains Dom.Hooks.catchUp) "catch-up indicator rendered"
                Expect.isTrue (html.Contains Dom.Hooks.connection) "connection status rendered"
                // ...and stays silent while it is brief, which is what stops the header
                // flickering green→blue→green on every message sent.
                Expect.isFalse
                    ((Support.render model).Contains Dom.Hooks.catchUp)
                    "a catch-up too brief to wait on says nothing"

                // A dropped channel moves the model to Reconnecting.
                do! channel.Close ()
                do! reconnecting
                Expect.equal model.Connection Reconnecting "dropped connection moves model to Reconnecting"
            }

        testCaseAsync "stop the Session Process host" <|
            async {
                match host with
                | Some h -> do! h.Stop ()
                | None -> ()
                // Deterministic teardown (no sleeps): the client's `channel.Close ()`
                // above waited for ITS PeerConnection to report closed, and `h.Stop ()`
                // drains every host-side PeerConnection the same way — so by here
                // libdatachannel owns no live objects and the global cleanup cannot race
                // a close callback (the SIGSEGV of verify run 30137017551).
                Interop.cleanup ()
            }
    ]
