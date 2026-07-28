module Yession.Tests.InMemory

// Cheap-tier end-to-end coverage that drives the REAL Session Process host over an
// in-memory channel pair (`host.Connect` + `App.connect`) instead of a WebRTC data channel.
// Same production code path — the peer handshake, the doc State relay, the queue drain, the
// cursor-presence relay, and the title→Manager report — but with no WebRTC, no HTTP, and no
// native addon, so it runs on every PR. Event-driven throughout via model waiters; the one
// host-side signal (the Manager report) is awaited through a one-shot continuation.
//
// These close the coverage gap left by the collaborative-title feature: the presence relay
// and the title report were previously reachable only through the verify tier.

open Fable.Pyxpecto
open Ylmish
open Yession.Domain
open Yession.App
open Yession.Host
open Yession.Tests.Support

let private sid () = SessionId.create "in-memory-session" |> expect

let tests =
    testList "In-memory transport (cheap E2E through the real Host)" [
        testCaseAsync "two clients converge on the title through the Host's State relay" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                // Ada names the session; the edit rides a State frame through the Host to Bob.
                a.Runner.Dispatch (user (EditTitleMsg (Text.insert 0 "launch plan" (a.Runner.Model ()).Synced.Title)))
                do! b.Runner.WaitFor (fun m -> Text.toString m.Synced.Title = "launch plan")
                Expect.equal (Text.toString (b.Runner.Model ()).Synced.Title) "launch plan" "B sees A's title via the Host relay"
                do! host.Stop ()
            }

        testCaseAsync "a sent rich draft drains through the Host into both timelines as its markdown body" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                // Ada composes a rich body and sends; the idle Host drains it to exactly one
                // MessageSent, snapshotted as MARKDOWN — visible in BOTH timelines (the sender's
                // too), queue cleared. Pins the send atomicity through the real drain: a split
                // send would either snapshot an empty body OR let the drain's removal Set clobber
                // the sender's freshly-consumed conversation (the top-level-root regression the
                // WebRTC E2E only caught in the verify tier).
                do! compose a ada "# ship it"
                a.Connection.SendDraft ada
                let settled (m: ClientModel) =
                    Map.isEmpty m.Synced.Queue
                    && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "# ship it" ]
                do! a.Runner.WaitFor settled
                do! b.Runner.WaitFor settled
                do! host.Stop ()
            }

        testCaseAsync "a peer's cursor presence relays to others in any field and clears on disconnect" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                // Ada's caret is in the title; the Host relays the presence frame to Bob.
                let titleFocus : Focus = { Field = Title; Pos = { Anchor = "AQI="; Head = "AwQ=" } }
                a.Connection.ReportPresence (Some titleFocus)
                do! b.Runner.WaitFor (fun m -> Map.containsKey a.Hello.PeerId m.Presence)
                Expect.equal
                    (Map.tryFind a.Hello.PeerId (b.Runner.Model ()).Presence |> Option.map (fun c -> c.Focus))
                    (Some titleFocus)
                    "B sees A's title caret with the reported anchor/head"
                // Ada moves into her own draft body: the field changes, and it still relays.
                let bodyFocus : Focus = { Field = DraftBody a.Hello.PeerId; Pos = { Anchor = "BQY="; Head = "BQY=" } }
                a.Connection.ReportPresence (Some bodyFocus)
                do! b.Runner.WaitFor (fun m ->
                        Map.tryFind a.Hello.PeerId m.Presence |> Option.map (fun c -> c.Focus) = Some bodyFocus)
                Expect.equal
                    (Map.tryFind a.Hello.PeerId (b.Runner.Model ()).Presence |> Option.map (fun c -> c.Focus.Field))
                    (Some (DraftBody a.Hello.PeerId))
                    "B sees A's caret move into the draft-body field"
                // Ada leaves; the Host clears her cursor on the remaining peer.
                do! a.Channel.Close ()
                do! b.Runner.WaitFor (fun m -> not (Map.containsKey a.Hello.PeerId m.Presence))
                Expect.isFalse (Map.containsKey a.Hello.PeerId (b.Runner.Model ()).Presence) "A's caret vanishes when she disconnects"
                do! host.Stop ()
            }

        testCaseAsync "the settled title is reported to the Manager hook" <|
            async {
                // One-shot: the report continuation is registered (via StartChild) BEFORE the
                // edit is dispatched, so the host-side report can never fire before we listen.
                let mutable reportCont : (string -> unit) option = None
                let awaitReport = Async.FromContinuations (fun (cont, _, _) -> reportCont <- Some cont)
                let report (name: string) = async { match reportCont with Some c -> reportCont <- None; c name | None -> () }

                let! host = Host.startFull (fun () -> None) None None None None (Some report) (fun _ _ -> ()) None None None (sid ()) None 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! reportWaiter = Async.StartChild awaitReport
                a.Runner.Dispatch (user (EditTitleMsg (Text.insert 0 "ship it" (a.Runner.Model ()).Synced.Title)))
                let! reported = reportWaiter
                Expect.equal reported "ship it" "the Host reports the settled title to the Manager hook"
                do! host.Stop ()
            }
    ]
