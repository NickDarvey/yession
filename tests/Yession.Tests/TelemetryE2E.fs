module Yession.Tests.TelemetryE2E

// The first telemetry e2e. Cheap tier: a real Session Process Host with the real telemetry
// emit sink, exporting over real localhost HTTP to a stub OTLP collector (the stand-in for a
// real OpenTelemetry Collector). A human message (injected the Phase-3 way — an offline peer's
// enqueue delivered into the Host's doc) drains into a turn; the scripted agent completes with
// usage; the sink ships it; the collector records it. No WebRTC, no native addon.
//
// This is the regression guard for the silent-no-op class: any break in the wire (sink not
// threaded, exporter miswired, decode mismatch) shows up here as zero — or wrong — records at
// the collector.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.App
open Yession.Host
open Yession.Tests.Support

/// A scripted agent that completes with fixed usage — the credential-free stand-in for a real
/// turn that reports token/cache counts.
let private usageAgent (usage: AgentUsage) : RunAgent =
    fun _ _ _ _ -> async { return AgentCompleted ("scripted turn", Some usage) }

let tests =
    testList "Telemetry E2E (real Host, stub collector, no WebRTC)" [
        testCaseAsync "a completed turn's usage reaches the collector over OTLP" <|
            async {
                // A stub OTLP collector on localhost — the stand-in for a real OTel Collector.
                let! stub = OtlpStub.start ()

                let sessionId = SessionId.create "e2e-telemetry" |> expect
                let usage =
                    { InputTokens = 123; OutputTokens = 45; CacheReadTokens = 6; CacheCreationTokens = 7
                      Model = Some "claude-opus-4-8" }
                let emitter = Telemetry.createOtlp sessionId stub.Url

                // A turn-complete signal taken off the emit sink itself: it fires exactly when
                // a completed turn reports usage, so no polling and no sleeps.
                let mutable fired = false
                let mutable waiter : (unit -> unit) option = None
                let sink turnId u =
                    emitter.Emit turnId u
                    fired <- true
                    match waiter with
                    | Some resume -> waiter <- None; resume ()
                    | None -> ()

                let! host = Host.startFull (fun () -> Some (usageAgent usage)) None None None None None None None sink None None None sessionId None "" None false 0

                // Inject a human message the Phase-3 way: an offline peer builds the enqueue on
                // its own doc, then we deliver that state into the Host's doc, which drains it
                // into one turn.
                let peerDoc = Y.Doc.Create ()
                peerDoc.clientID <- 7.0
                let peerRegistry = BodyRegistry peerDoc
                let peerRunner = Harness.run (App.makeProgram peerDoc (ClientModel.init (peer "ada" "Ada")))
                let peerId = (peerRunner.Model ()).Peer.PeerId
                Body.authorAs (QueueId.create "q-1" |> expect) peerRegistry peerRunner peerId "trigger a turn"
                Body.send peerRegistry peerRunner peerId |> ignore
                Y.applyUpdate (host.Doc, Y.encodeStateAsUpdate peerDoc)

                // Wait for the turn's usage to be emitted, then flush the batch exporter so the
                // record has certainly reached the receiver.
                do! Async.FromContinuations (fun (cont, _, _) -> if fired then cont () else waiter <- Some cont)
                do! emitter.Shutdown () |> Async.AwaitPromise

                match stub.Received () |> List.choose OtlpStub.turnUsage with
                | [ u ] ->
                    Expect.equal u.SessionId "e2e-telemetry" "the record is tagged with the session id"
                    Expect.equal
                        (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens)
                        (123, 45, 6, 7)
                        "the turn's counts crossed the wire intact"
                    Expect.equal u.Model (Some "claude-opus-4-8") "the model crossed the wire"
                    Expect.isFalse (System.String.IsNullOrEmpty u.TurnId) "a turn id is present"
                | other -> failwithf "expected exactly one usage record at the collector, got %d" (List.length other)

                do! host.Stop ()
                stub.Close ()
            }
    ]
