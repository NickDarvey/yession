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

        testCaseAsync "two peers co-edit one draft and either can send it — once" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                // Ada starts a message; Bob's composer opens on it rather than on a rival blank.
                do! compose a ada "we should ask it to"
                do! b.Runner.WaitFor (fun m -> Map.containsKey ada m.Synced.Drafts)
                Expect.equal
                    (ClientModel.composerTarget (b.Runner.Model ())) ada
                    "the draft already in flight IS Bob's composer"

                // Bob edits ADA's draft — the co-edit the read-only mirror used to forbid — and
                // the words converge on both replicas.
                do! compose b ada "we should ask it to re-run the migration"
                do! a.Runner.WaitFor (fun _ -> draftBody a ada = Some "we should ask it to re-run the migration")

                // Bob sends Ada's draft. One message, attributed to Ada, and the slot clears
                // everywhere: the send key came from the draft, not from Bob.
                b.Connection.SendDraft ada
                let settled (m: ClientModel) =
                    Map.isEmpty m.Synced.Queue
                    && not (Map.containsKey ada m.Synced.Drafts)
                    && (m.Conversation.Items |> List.map (fun i -> i.Body)) = [ "we should ask it to re-run the migration" ]
                do! a.Runner.WaitFor settled
                do! b.Runner.WaitFor settled
                let! page = host.Log.Read None System.Int32.MaxValue
                let sent =
                    page.Events
                    |> List.choose (fun e -> match e.Event with MessageSent m -> Some m | _ -> None)
                match sent with
                | [ message ] ->
                    Expect.equal message.Author (PeerRef ada) "attributed to the peer whose draft it was"
                    Expect.equal message.Body "we should ask it to re-run the migration" "the co-written body"
                | other -> failwithf "expected exactly one MessageSent, got %A" other
                do! host.Stop ()
            }

        testCaseAsync "a draft box appears only for a peer that has typed, and goes when its composer empties" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                // Ada types: her slot publishes and reaches Bob through the Host relay. Bob's
                // composer renders one box per slot, so his slot map IS what he shows.
                do! compose a ada "thinking out loud"
                do! b.Runner.WaitFor (fun m -> Map.containsKey ada m.Synced.Drafts)
                // Bob has been connected the whole time and never typed. Before publication
                // followed the body, merely mounting a composer published a slot — so every peer
                // that had ever opened the session showed an empty draft box on everyone's
                // composer, forever.
                Expect.equal
                    ((b.Runner.Model ()).Synced.Drafts |> Map.toList |> List.map fst)
                    [ ada ]
                    "the idle peer contributes no draft box"
                Expect.equal
                    ((a.Runner.Model ()).Synced.Drafts |> Map.toList |> List.map fst)
                    [ ada ]
                    "and none for itself on its own replica"
                // Ada empties her composer: the box goes away on Bob too.
                do! clearComposer a ada
                do! b.Runner.WaitFor (fun m -> not (Map.containsKey ada m.Synced.Drafts))
                do! host.Stop ()
            }

        testCaseAsync "discarding a draft empties the composer, not just the slot" <|
            async {
                let! host = Host.start (sid ()) 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! b = connectInMemoryClient host "bob" "Bob"
                let ada = a.Hello.PeerId
                do! compose a ada "never mind, wrong session"
                do! b.Runner.WaitFor (fun m -> Map.containsKey ada m.Synced.Drafts)

                // Discard is what the composer's ✕ does. It has to reach the BODY: the slot is
                // republished by the publication rule from whatever the body holds, so removing
                // the slot alone left the words on screen and the next keystroke put the draft
                // straight back — the button changed nothing a person could see.
                a.Connection.DiscardDraft ada
                do! a.Runner.WaitFor (fun m -> not (Map.containsKey ada m.Synced.Drafts))
                do! b.Runner.WaitFor (fun m -> not (Map.containsKey ada m.Synced.Drafts))
                Expect.equal (draftBody a ada) (Some "") "the composer is empty on the author's replica"
                Expect.equal (draftBody b ada) (Some "") "and on everyone else's"

                // And it stays discarded: with the body empty, the rule has nothing to publish.
                do! compose a ada "a fresh thought"
                Expect.equal (draftBody a ada) (Some "a fresh thought") "typing again starts a new draft, not the discarded one"
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

                let! host = Host.startFull (fun () -> None) None None None None (Some report) None (fun _ _ -> ()) None None None (sid ()) None "" None false 0
                let! a = connectInMemoryClient host "ada" "Ada"
                let! reportWaiter = Async.StartChild awaitReport
                a.Runner.Dispatch (user (EditTitleMsg (Text.insert 0 "ship it" (a.Runner.Model ()).Synced.Title)))
                let! reported = reportWaiter
                Expect.equal reported "ship it" "the Host reports the settled title to the Manager hook"
                do! host.Stop ()
            }

        // Plan 11's dangerous direction. Reaping an idle session costs a relaunch; reaping a
        // session someone is USING costs their turn and their connection, so the property
        // that has to hold is that a connected peer keeps the session busy. `Reaper.plan`
        // covers the Manager's half (it never reaps a launch reported busy); this covers the
        // half only the Host knows — that a peer arriving and leaving is what moves the
        // report.
        //
        // Synchronised on the Host's OWN completion points, not on the reports under test:
        // `connectInMemoryClient` returns once the peer is accepted, and
        // `WaitForNextSessionEnd` resolves once a peer session has torn down — and the busy
        // report is made inside each of those, before they complete. So every assertion here
        // reads a list that is already final.
        //
        // That distinction is what makes this diagnose rather than merely detect. An earlier
        // version awaited the REPORT itself; break the busy signal and it hung until the
        // suite's 240s timeout, which reads as infrastructure trouble rather than as the
        // property having broken. Waiting on something independent of the thing under test
        // means a regression fails an assertion, by name, immediately.
        testCaseAsync "a connected peer holds the session busy; the LAST one leaving releases it" <|
            async {
                let reports = ResizeArray<bool> ()
                let report (busy: bool) = async { reports.Add busy }

                let! host =
                    Host.startFull (fun () -> None) None None None None None (Some report) (fun _ _ -> ()) None None None (sid ()) None "" None false 0

                // A session nobody has attached to is idle from the moment it boots — which
                // is what lets the Manager's window start at launch rather than at first
                // visitor, and what makes an abandoned launch collectable at all.
                Expect.equal (List.ofSeq reports) [ false ] "a freshly booted session reports itself idle"

                let! ada = connectInMemoryClient host "ada" "Ada"
                Expect.equal (Seq.last reports) true "a peer joining makes the session report busy"

                // A second peer joins and the first leaves: still occupied, so the session
                // must NOT release. This is the case a naive "someone disconnected, so we are
                // idle" gets wrong, and it is exactly how a reap lands on a live user.
                let! bob = connectInMemoryClient host "bob" "Bob"
                let departed = host.WaitForNextSessionEnd ()
                do! ada.Channel.Close ()
                do! departed
                Expect.isFalse
                    (Seq.contains false (Seq.skip 1 reports))
                    "with a peer still attached, a departure must never report idle"

                // The last one leaves, and only now does it release.
                let lastDeparted = host.WaitForNextSessionEnd ()
                do! bob.Channel.Close ()
                do! lastDeparted
                Expect.equal (Seq.last reports) false "the last peer leaving reports the session idle"

                do! host.Stop ()
            }
    ]
